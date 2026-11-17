namespace Kombin.Api.Features.TryOn.Rules;

/// <summary>What the app does: show the image, show "Hazırlanıyor", or hide the action entirely.</summary>
internal enum TryOnStatus
{
    Ready,
    Preparing,
    Unavailable,
}

/// <summary>Why the action is hidden (for the app team and support; the app hides it whatever the reason).</summary>
internal enum TryOnUnavailableReason
{
    /// <summary>The body's BMI is over <c>TryOn:MaxBmi</c>: the outfit's edges would run off the picture.</summary>
    BodyOutOfRange,

    /// <summary>A piece has no category, so its family is unknown.</summary>
    UnknownCategory,

    /// <summary>No top, outerwear (or dress).</summary>
    NoTop,

    /// <summary>No trousers or skirt (or dress): the figure is never drawn with bare legs.</summary>
    NoBottom,

    /// <summary>A core wardrobe piece was photographed on a person (<c>person_wearing</c>): never prepared.</summary>
    PersonInPhoto,

    /// <summary>A core piece cannot be prepared (archived, no image), was refused, failed, or was answered <c>reliable = false</c>.</summary>
    PieceNotReady,

    /// <summary>The outermost upper garment is a top with sleeves folded across the front.</summary>
    FoldedSleeves,

    /// <summary>The renderer refused the outfit (any <c>422</c>); never retried.</summary>
    RendererRefused,
}

/// <summary>Where a piece of the outfit stands, as the request found it.</summary>
internal enum TryOnPieceState
{
    /// <summary>No garment row yet: a prepare is due.</summary>
    NoRow,

    /// <summary>A row without an answer, waiting for its first try or its one retry.</summary>
    Pending,

    /// <summary>Answered: <see cref="TryOnPieceFacts.Reliable"/> and <see cref="TryOnPieceFacts.MayBeOutermost"/> are the answer's.</summary>
    Prepared,

    /// <summary>Refused, failed, or not preparable at all (archived, no photo, no catalogue image).</summary>
    NotReady,

    /// <summary>A wardrobe photo of someone wearing it: never prepared.</summary>
    PersonInPhoto,
}

/// <summary>
/// One piece: <c>Family</c> is the answer's when prepared, else its category's (null: no category, or one without a family). <c>PieceId</c> is
/// the outfit's ID for it (wardrobe item or product).
/// </summary>
internal sealed record TryOnPieceFacts(Guid PieceId, TryOnFamily? Family, TryOnPieceState State, bool Reliable = false, bool? MayBeOutermost = null);

/// <summary><c>Prepare</c>: the pieces without an answer that a request should ask for (bags and belts too, though they never block).</summary>
internal sealed record TryOnDecision(TryOnStatus Status, TryOnUnavailableReason? Reason, IReadOnlyList<Guid> Prepare)
{
    public static TryOnDecision Unavailable(TryOnUnavailableReason reason) => new(TryOnStatus.Unavailable, reason, []);
}

/// <summary>
/// The rule at the top of the try-on brief, pure (<c>docs/design/try-on.md</c>, "Readiness"). In order:
/// <list type="number">
/// <item>the body's BMI over the limit → Unavailable (whatever the outfit);</item>
/// <item>a piece without a family → Unavailable; no top (top or outerwear) or no bottom (trousers or skirt) → Unavailable;</item>
/// <item>a core piece (top, outerwear, trousers, skirt, shoes) photographed on a person, refused, failed, not preparable or answered
/// <c>reliable = false</c> → Unavailable; bags and belts never block;</item>
/// <item>the outermost upper garment (the outerwear if any, else the top) answered <c>may_be_outermost = false</c> → Unavailable; a top under
/// outerwear is fine, outerwear is never restricted;</item>
/// <item>a core piece without an answer yet → Preparing;</item>
/// <item>otherwise Ready.</item>
/// </list>
/// The renderer enforces the same rule again (its 422s), so a disagreement costs one call, never a wrong picture.
/// </summary>
internal static class TryOnReadiness
{
    public static TryOnDecision Decide(IReadOnlyList<TryOnPieceFacts> pieces, double bmi, double maxBmi)
    {
        ArgumentNullException.ThrowIfNull(pieces);

        if (bmi > maxBmi)
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.BodyOutOfRange);
        }

        if (pieces.Any(piece => piece.Family is null))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.UnknownCategory);
        }

        if (!pieces.Any(piece => TryOnFamilies.IsUpper(piece.Family!.Value)))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.NoTop);
        }

        if (!pieces.Any(piece => TryOnFamilies.IsBottom(piece.Family!.Value)))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.NoBottom);
        }

        var core = pieces.Where(piece => TryOnFamilies.IsCore(piece.Family!.Value)).ToList();

        if (core.Any(piece => piece.State == TryOnPieceState.PersonInPhoto))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.PersonInPhoto);
        }

        if (core.Any(piece => piece.State == TryOnPieceState.NotReady || (piece.State == TryOnPieceState.Prepared && !piece.Reliable)))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.PieceNotReady);
        }

        // The outermost upper garment is the outerwear when there is any (never restricted), else the top.
        var outermostIsTop = !core.Any(piece => piece.Family == TryOnFamily.Outerwear);
        if (outermostIsTop && core.Any(piece => piece.Family == TryOnFamily.Top && piece.State == TryOnPieceState.Prepared && piece.MayBeOutermost == false))
        {
            return TryOnDecision.Unavailable(TryOnUnavailableReason.FoldedSleeves);
        }

        var unanswered = pieces.Where(piece => piece.State is TryOnPieceState.NoRow or TryOnPieceState.Pending).Select(piece => piece.PieceId).ToList();

        return core.Any(piece => piece.State is TryOnPieceState.NoRow or TryOnPieceState.Pending)
            ? new TryOnDecision(TryOnStatus.Preparing, null, unanswered)
            : new TryOnDecision(TryOnStatus.Ready, null, unanswered);
    }
}
