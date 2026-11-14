namespace Kombin.Api.Features.Identity;

/// <summary>The rules of account deletion, pure (unit-tested in <c>AccountDeletionRulesTests</c>).</summary>
internal static class AccountDeletionRules
{
    /// <summary>How long a deletion request can be taken back; the <c>account-deletion</c> job removes the account for good after it.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>The word the user types to confirm (Turkish "delete", with the dotted capital İ).</summary>
    public const string ConfirmWord = "SİL";

    /// <summary>When the data of an account that asked for deletion at <paramref name="requestedAt"/> is gone.</summary>
    public static DateTimeOffset ScheduledFor(DateTimeOffset requestedAt) => requestedAt + Window;

    /// <summary>Whether the window is over and the job may delete the account.</summary>
    public static bool IsDue(DateTimeOffset? requestedAt, DateTimeOffset now) => requestedAt is { } at && now >= ScheduledFor(at);

    /// <summary>Whether the user can still change their mind: a request exists and its window is not over.</summary>
    public static bool CanCancel(DateTimeOffset? requestedAt, DateTimeOffset now) => requestedAt is not null && !IsDue(requestedAt, now);

    /// <summary>
    /// "SİL", "sil", "SIL" or "Sil", with spaces around: a phone keyboard in Turkish or English makes any of them, and the runtime image may
    /// run with invariant globalization (no Turkish casing), so the dotted and dotless i are folded by hand.
    /// </summary>
    public static bool ConfirmationMatches(string? confirm)
    {
        if (string.IsNullOrWhiteSpace(confirm))
        {
            return false;
        }

        var folded = new string([.. confirm.Trim().Select(letter => letter is 'İ' or 'i' or 'ı' or 'I' ? 'I' : char.ToUpperInvariant(letter))]);

        return folded == "SIL";
    }
}
