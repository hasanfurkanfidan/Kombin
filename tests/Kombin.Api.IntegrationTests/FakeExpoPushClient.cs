using System.Collections.Concurrent;
using Kombin.Api.Features.Notification;

namespace Kombin.Api.IntegrationTests;

/// <summary>
/// The Expo push service for the tests. The integration tests share one database and one host, and the jobs process whatever is
/// due, so the fake keeps no per-test script: what it answers depends only on the token in the message.
/// <list type="bullet">
/// <item><c>http-fail</c> in the token: the whole call fails, as if Expo were down.</item>
/// <item><c>gone</c>: an error ticket <c>DeviceNotRegistered</c>.</item>
/// <item><c>toobig</c>: an error ticket <c>MessageTooBig</c>.</item>
/// <item>anything else: an ok ticket whose ID is <c>ticket-</c> plus the token's inner part.</item>
/// </list>
/// Receipts follow the ticket ID: <c>receipt-gone</c> → <c>DeviceNotRegistered</c>, <c>receipt-err</c> → <c>MessageRateExceeded</c>,
/// <c>not-ready</c> → missing, anything else ok.
/// </summary>
internal sealed class FakeExpoPushClient : IExpoPushClient
{
    private readonly ConcurrentQueue<ExpoPushMessage> _messages = new();

    private readonly ConcurrentQueue<IReadOnlyList<ExpoPushMessage>> _batches = new();

    /// <summary>Every message sent through the fake, oldest first. Filter by your own token.</summary>
    public IReadOnlyList<ExpoPushMessage> Messages => [.. _messages];

    /// <summary>Every call to <see cref="SendAsync"/>, oldest first.</summary>
    public IReadOnlyList<IReadOnlyList<ExpoPushMessage>> Batches => [.. _batches];

    public static string TicketIdFor(string token) => $"ticket-{token[(token.IndexOf('[', StringComparison.Ordinal) + 1)..^1]}";

    public Task<ExpoSendResult> SendAsync(IReadOnlyList<ExpoPushMessage> messages, CancellationToken cancellationToken)
    {
        _batches.Enqueue(messages);

        foreach (var message in messages)
        {
            _messages.Enqueue(message);
        }

        // One bad token fails the whole call, like an outage; only tests that use such a token are affected.
        if (messages.Any(message => message.To.Contains("http-fail", StringComparison.Ordinal)))
        {
            return Task.FromResult(ExpoSendResult.Failed("HTTP 503: service unavailable"));
        }

        var tickets = messages.Select(message => message.To switch
        {
            var to when to.Contains("gone", StringComparison.Ordinal) =>
                new ExpoTicket("error", null, "The recipient device is not registered with FCM.", "DeviceNotRegistered"),
            var to when to.Contains("toobig", StringComparison.Ordinal) =>
                new ExpoTicket("error", null, "Message too big.", "MessageTooBig"),
            var to => new ExpoTicket("ok", TicketIdFor(to), null, null),
        }).ToList();

        return Task.FromResult(new ExpoSendResult(tickets, null));
    }

    public Task<ExpoReceiptsResult> GetReceiptsAsync(IReadOnlyList<string> ticketIds, CancellationToken cancellationToken)
    {
        var receipts = new Dictionary<string, ExpoReceipt>();

        foreach (var id in ticketIds.Where(id => !id.Contains("not-ready", StringComparison.Ordinal)))
        {
            receipts[id] = id switch
            {
                var gone when gone.Contains("receipt-gone", StringComparison.Ordinal) =>
                    new ExpoReceipt("error", "The recipient device is not registered with FCM.", "DeviceNotRegistered"),
                var failed when failed.Contains("receipt-err", StringComparison.Ordinal) =>
                    new ExpoReceipt("error", "Too many messages.", "MessageRateExceeded"),
                _ => new ExpoReceipt("ok", null, null),
            };
        }

        return Task.FromResult(new ExpoReceiptsResult(receipts, null));
    }
}
