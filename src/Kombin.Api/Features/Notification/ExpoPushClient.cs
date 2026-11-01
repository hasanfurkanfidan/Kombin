using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Kombin.Api.Features.Notification;

/// <summary><c>AccessToken</c> is only needed when "enhanced push security" is switched on for the Expo project.</summary>
internal sealed class ExpoOptions
{
    public const string SectionName = "Expo";

    public string? AccessToken { get; set; }
}

/// <summary>One push message. <c>Data</c> is the notification's JSON object, delivered to the app as the notification payload.</summary>
internal sealed record ExpoPushMessage(string To, string Title, string Body, JsonElement Data, string Sound = "default", string Priority = "high");

/// <summary>Expo's answer for one message: <c>Status</c> is <c>ok</c> (then <c>Id</c> is the ticket) or <c>error</c> (then <c>Error</c> is e.g. <c>DeviceNotRegistered</c>).</summary>
internal sealed record ExpoTicket(string Status, string? Id, string? Message, string? Error)
{
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ExpoReceipt(string Status, string? Message, string? Error)
{
    public bool IsOk => string.Equals(Status, "ok", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Outcome of a call to the push service. <c>Error</c> is set when the call as a whole failed (network, non-2xx, unreadable body);
/// then <c>Tickets</c> is empty. Individual messages can still fail inside a successful call.
/// </summary>
internal sealed record ExpoSendResult(IReadOnlyList<ExpoTicket> Tickets, string? Error)
{
    public bool Succeeded => Error is null;

    public static ExpoSendResult Failed(string error) => new([], error);
}

internal sealed record ExpoReceiptsResult(IReadOnlyDictionary<string, ExpoReceipt> Receipts, string? Error)
{
    public bool Succeeded => Error is null;

    public static ExpoReceiptsResult Failed(string error) => new(new Dictionary<string, ExpoReceipt>(), error);
}

/// <summary>The Expo push service. Implementations never throw for a failed call; they return the failure so the job can decide.</summary>
internal interface IExpoPushClient
{
    /// <summary>Sends up to 100 messages. On success <c>Tickets</c> has one entry per message, in order.</summary>
    Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken);

    /// <summary>Asks for up to 300 receipts; receipts that are not ready yet are simply missing from the result.</summary>
    Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken);
}

internal sealed partial class ExpoPushClient(HttpClient http, IOptions<ExpoOptions> options, ILogger<ExpoPushClient> logger) : IExpoPushClient
{
    public const string SendUrl = "https://exp.host/--/api/v2/push/send";

    public const string ReceiptsUrl = "https://exp.host/--/api/v2/push/getReceipts";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken)
    {
        var (body, error) = await PostAsync(SendUrl, messages, cancellationToken);
        if (body is not { } root)
        {
            return ExpoSendResult.Failed(error ?? "No response.");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return ExpoSendResult.Failed("Unexpected response shape.");
        }

        var tickets = data.EnumerateArray().Select(ReadTicket).ToList();

        // One ticket per message, in order: anything else cannot be matched back to the messages.
        return tickets.Count == messages.Count
            ? new ExpoSendResult(tickets, null)
            : ExpoSendResult.Failed($"Expected {messages.Count} tickets, got {tickets.Count}.");
    }

    public async Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken)
    {
        var (body, error) = await PostAsync(ReceiptsUrl, new { ids = ticketIds }, cancellationToken);
        if (body is not { } root)
        {
            return ExpoReceiptsResult.Failed(error ?? "No response.");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return ExpoReceiptsResult.Failed("Unexpected response shape.");
        }

        var receipts = data.EnumerateObject().ToDictionary(
            property => property.Name,
            property => new ExpoReceipt(
                ReadString(property.Value, "status") ?? "error",
                ReadString(property.Value, "message"),
                ReadError(property.Value)));

        return new ExpoReceiptsResult(receipts, null);
    }

    /// <summary>Posts JSON and returns the parsed body, or an error text; never throws except when the caller cancels.</summary>
    private async Task<(JsonElement? Body, string? Error)> PostAsync<TBody>(string url, TBody payload, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload, options: Json) };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            if (!string.IsNullOrWhiteSpace(options.Value.AccessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.AccessToken);
            }

            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var reason = $"HTTP {(int)response.StatusCode}: {Summarize(text)}";
                LogCallFailed(logger, url, reason);

                return (null, reason);
            }

            using var document = JsonDocument.Parse(text);

            return (document.RootElement.Clone(), null);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException or IOException)
        {
            // A caller-requested cancellation is not a failure of the push service.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            LogCallFailed(logger, url, exception.Message);

            return (null, exception.Message);
        }
    }

    private static ExpoTicket ReadTicket(JsonElement element) =>
        new(ReadString(element, "status") ?? "error", ReadString(element, "id"), ReadString(element, "message"), ReadError(element));

    private static string? ReadError(JsonElement element) =>
        element.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object ? ReadString(details, "error") : null;

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Summarize(string text) => text.Length <= 200 ? text : text[..200];

    [LoggerMessage(Level = LogLevel.Warning, Message = "Expo push call {Url} failed: {Reason}")]
    private static partial void LogCallFailed(ILogger logger, string url, string reason);
}
