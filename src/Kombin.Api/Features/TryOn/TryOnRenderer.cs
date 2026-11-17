using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kombin.Api.Common.Storage;
using Kombin.Api.Common.Vision;
using Kombin.Api.Features.Profile;
using Kombin.Api.Features.TryOn.Rules;
using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Catalog;
using Kombin.Api.Persistence.Entities.TryOn;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kombin.Api.Features.TryOn;

/// <summary>The fit label of one piece: <c>Tight</c> ("Dar gelebilir"), <c>TrueToSize</c> ("Tam bedenin"), <c>Loose</c> ("Bol gelebilir").</summary>
internal enum TryOnFitLevel
{
    Tight,
    TrueToSize,
    Loose,
}

/// <summary>One piece of a drawing: <c>Id</c> is the piece's ID as the outfit carries it (wardrobe item or product).</summary>
internal sealed record TryOnGarmentResponse(Guid Id, TryOnFitLevel? FitLevel);

internal enum RenderVerdict
{
    Rendered,

    /// <summary>One of the user's renders is running.</summary>
    InFlight,

    /// <summary>The hourly limit; <see cref="RenderResult.RetryAfter"/> says when a slot frees.</summary>
    OverLimit,

    /// <summary>A 4xx of the renderer (its 422s above all): the action is hidden, never retried.</summary>
    Refused,

    /// <summary>The renderer could not be reached (503 <c>busy</c>, 5xx, network).</summary>
    ServiceDown,
}

internal sealed record RenderResult(RenderVerdict Verdict, TryOnRender? Render = null, TimeSpan RetryAfter = default);

/// <summary>
/// Draws a ready outfit (<c>docs/design/try-on.md</c>, "The endpoints"): the cache first (a hit is free and moves <c>last_used_at</c>), then
/// <see cref="TryOnRenderGate"/>, then <c>/v1/tryon/render</c> with the body, each prepared garment's answer, its size, the size hint's size and
/// the cut, and the prepared cutouts in the outfit's order. A new render is stored privately and replaces the user's renders of other
/// measurements. Body values reach the vision service and nothing else (no log line, no event).
/// </summary>
internal sealed partial class TryOnRenderer(
    KombinDbContext db,
    IObjectStorage storage,
    IVisionClient vision,
    TryOnRenderGate gate,
    IOptions<TryOnOptions> options,
    ILogger<TryOnRenderer> logger)
{
    /// <summary>How <see cref="TryOnRender.GarmentsJson"/> is written and read: camelCase, enums by name.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Sent(TryOnPiece Piece, TryOnGarment Row, string? Size, string? ReferenceSize, string? Cut);

    public async Task<RenderResult> RenderAsync(Guid userId, TryOnBody body, IReadOnlyList<TryOnPiece> pieces, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(pieces);

        // Bags and belts without an answer are left out; every core piece is prepared and reliable, or the request would not be here.
        var sent = new List<Sent>();
        foreach (var piece in pieces.Where(piece => piece.Row is { Status: TryOnGarmentStatus.Prepared, CutoutKey: not null }))
        {
            sent.Add(new Sent(
                piece,
                piece.Row!,
                piece.Size,
                await ReferenceSizeAsync(userId, piece, cancellationToken),
                piece.Fit == ProductFit.Unknown ? null : piece.Fit.ToString()));
        }

        var settings = options.Value;
        var renderer = await vision.GetRendererAsync(cancellationToken) ?? "unknown";
        var key = TryOnCacheKey.For(
            renderer,
            body,
            [.. sent.Select(garment => new CacheKeyGarment(garment.Row.Id, garment.Row.PrepareVersion, garment.Row.PreparedAt, garment.Row.PreparedHash, garment.Size, garment.ReferenceSize, garment.Cut))],
            settings.RenderWidth);

        var cached = await db.TryOnRenders.SingleOrDefaultAsync(render => render.UserId == userId && render.CacheKey == key, cancellationToken);
        if (cached is not null)
        {
            cached.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return new RenderResult(cached.Refusal is null ? RenderVerdict.Rendered : RenderVerdict.Refused, cached);
        }

        var admitted = gate.TryStart(userId, now);
        if (admitted.Verdict != RenderGateVerdict.Started)
        {
            return admitted.Verdict == RenderGateVerdict.InFlight
                ? new RenderResult(RenderVerdict.InFlight)
                : new RenderResult(RenderVerdict.OverLimit, RetryAfter: admitted.RetryAfter);
        }

        try
        {
            var cutouts = new List<byte[]>();
            foreach (var garment in sent)
            {
                var cutout = await storage.GetAsync(garment.Row.CutoutKey!, cancellationToken);
                if (cutout is null)
                {
                    LogCutoutMissing(logger, garment.Row.Id);
                    return new RenderResult(RenderVerdict.Refused);
                }

                cutouts.Add(cutout.Content);
            }

            var call = await vision.RenderAsync(Spec(body, sent, settings.RenderWidth), cutouts, cancellationToken);
            if (call.Value is not { } answer)
            {
                LogRenderFailed(logger, call.Status ?? 0, call.Detail ?? "unreachable");
                if (call.IsTransient)
                {
                    return new RenderResult(RenderVerdict.ServiceDown);
                }

                // Every 4xx (incomplete_outfit, garment_not_ready, folded_sleeves, body_out_of_range above all) hides the action and is never
                // retried: remembered under the key, so the same inputs do not ask again.
                var refusal = new TryOnRender
                {
                    UserId = userId,
                    CacheKey = key,
                    BodyHash = body.Hash(),
                    Renderer = Clip(renderer, TryOnRender.MaxRendererLength),
                    Refusal = Clip(call.Detail ?? $"http_{call.Status}", 40),
                    CreatedAt = now,
                    LastUsedAt = now,
                };
                db.TryOnRenders.Add(refusal);
                await SaveOnceAsync(cancellationToken);

                return new RenderResult(RenderVerdict.Refused, refusal);
            }

            byte[] image;
            try
            {
                image = Convert.FromBase64String(answer.Image);
            }
            catch (FormatException)
            {
                return new RenderResult(RenderVerdict.ServiceDown);
            }

            var png = string.Equals(answer.Format, "png", StringComparison.OrdinalIgnoreCase);
            var render = new TryOnRender
            {
                UserId = userId,
                CacheKey = key,
                BodyHash = body.Hash(),
                Renderer = Clip(answer.Renderer, TryOnRender.MaxRendererLength),
                Width = answer.Width,
                Height = answer.Height,
                DefaultedMeasurements = [.. answer.DefaultedMeasurements],
                CreatedAt = now,
                LastUsedAt = now,
            };
            render.ImageKey = StorageKeys.Build(TryOnPreparer.StorageArea, userId, render.Id, ImageVariant.Render, png ? "png" : "webp");
            render.GarmentsJson = JsonSerializer.Serialize(
                sent.Select((garment, index) => new TryOnGarmentResponse(
                    garment.Piece.Ref.Id,
                    Enum.TryParse<TryOnFitLevel>(answer.Garments.FirstOrDefault(fit => fit.Index == index)?.FitLevel, out var level) ? level : null)),
                Json);

            await storage.PutAsync(render.ImageKey, image, png ? "image/png" : "image/webp", cancellationToken);
            db.TryOnRenders.Add(render);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // The same outfit was drawn by another instance a moment ago: serve that one, drop ours.
                db.ChangeTracker.Clear();
                await storage.DeleteAsync([render.ImageKey], cancellationToken);
                return new RenderResult(RenderVerdict.Rendered, await db.TryOnRenders.AsNoTracking().SingleAsync(row => row.UserId == userId && row.CacheKey == key, cancellationToken));
            }

            await DeleteOtherBodiesAsync(userId, render.BodyHash, cancellationToken);

            return new RenderResult(RenderVerdict.Rendered, render);
        }
        finally
        {
            gate.Finish(userId);
        }
    }

    /// <summary>The user's renders of other measurements can never be served again and show proportions they no longer have: deleted at once.</summary>
    private async Task DeleteOtherBodiesAsync(Guid userId, string bodyHash, CancellationToken cancellationToken)
    {
        var stale = await db.TryOnRenders.AsNoTracking()
            .Where(render => render.UserId == userId && render.BodyHash != bodyHash)
            .Select(render => new { render.Id, render.ImageKey })
            .ToListAsync(cancellationToken);
        if (stale.Count == 0)
        {
            return;
        }

        await storage.DeleteAsync([.. stale.Select(render => render.ImageKey).OfType<string>()], cancellationToken);
        var ids = stale.Select(render => render.Id).ToList();
        await db.TryOnRenders.Where(render => ids.Contains(render.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Stores a refusal; a parallel request that stored the same key first wins, which is the same answer.</summary>
    private async Task SaveOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }
    }

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..length];

    /// <summary>The size hint's own size for the piece's brand and group, else its suggestion (the brief); none without a brand.</summary>
    private async Task<string?> ReferenceSizeAsync(Guid userId, TryOnPiece piece, CancellationToken cancellationToken)
    {
        if (piece.BrandId is not { } brandId || piece.CategorySlug is not { } slug)
        {
            return null;
        }

        var hint = await SizeHints.BuildAsync(db, userId, brandId, slug, cancellationToken);

        return hint.Size ?? hint.Suggestion?.Size;
    }

    /// <summary>The render spec (README, <c>/v1/tryon/render</c>): landmarks go back exactly as the service sent them.</summary>
    private static string Spec(TryOnBody body, IReadOnlyList<Sent> sent, int width)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("body");
            writer.WriteString("audience", body.ServiceAudience);
            WriteNumber(writer, "heightCm", body.HeightCm);
            WriteNumber(writer, "weightKg", body.WeightKg);
            WriteNumber(writer, "chestCm", body.ChestCm);
            WriteNumber(writer, "waistCm", body.WaistCm);
            WriteNumber(writer, "hipCm", body.HipCm);
            writer.WriteNull("shoulderCm");
            writer.WriteEndObject();

            writer.WriteStartArray("garments");
            foreach (var garment in sent)
            {
                writer.WriteStartObject();
                writer.WriteString("family", garment.Row.Family);
                writer.WritePropertyName("landmarks");
                writer.WriteRawValue(garment.Row.LandmarksJson ?? "{}");
                writer.WriteBoolean("reliable", garment.Row.Reliable);
                writer.WriteString("source", garment.Row.Source);
                writer.WriteStartArray("flags");
                foreach (var flag in garment.Row.Flags)
                {
                    writer.WriteStringValue(flag);
                }

                writer.WriteEndArray();
                writer.WriteString("cut", garment.Cut);
                writer.WriteString("size", garment.Size);
                writer.WriteString("referenceSize", garment.ReferenceSize);
                writer.WriteNull("chartCm");
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteNumber("width", width);
            writer.WriteString("format", "webp");
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void WriteNumber(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Try-on render refused or failed: HTTP {Status}, {Detail}.")]
    private static partial void LogRenderFailed(ILogger logger, int status, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Try-on garment {GarmentId} has no stored cutout; the outfit is not drawn.")]
    private static partial void LogCutoutMissing(ILogger logger, Guid garmentId);
}
