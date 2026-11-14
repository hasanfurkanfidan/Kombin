using Hangfire;
using Kombin.Api.Common.Time;
using Kombin.Api.Persistence;

namespace Kombin.Api.Features.Identity.Jobs;

internal readonly record struct AccountDeletionSummary(int AccountsDeleted, int StorageFailures, int ObjectsDeleted);

/// <summary>
/// Hourly: removes the accounts whose deletion window (<see cref="AccountDeletionRules.Window"/>, 7 days) is over, one by one through
/// <see cref="AccountPurger"/>. Idempotent and resumable: a deleted account no longer matches; an account whose objects could not be deleted
/// stays untouched and is tried again by the next run (hourly, so the 7-day promise holds even if a few runs fail). One failing account
/// never stops the others.
/// </summary>
internal sealed partial class AccountDeletionJob(KombinDbContext db, AccountPurger purger, IClock clock, ILogger<AccountDeletionJob> logger)
{
    public const string JobId = "account-deletion";

    /// <summary>Hourly at minute 43 (the zone is set where the job is registered).</summary>
    public const string Cron = "43 * * * *";

    public const int BatchSize = 50;

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var summary = await DeleteDueAsync(null, cancellationToken);

        LogRun(logger, summary.AccountsDeleted, summary.ObjectsDeleted, summary.StorageFailures);
    }

    /// <summary><paramref name="onlyUsers"/> restricts the run to those accounts (tests share a database).</summary>
    public async Task<AccountDeletionSummary> DeleteDueAsync(IReadOnlyCollection<Guid>? onlyUsers, CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - AccountDeletionRules.Window;
        var deleted = 0;
        var objects = 0;
        var failed = new List<Guid>();

        while (true)
        {
            var due = db.Users.Where(user => user.DeletionRequestedAt != null && user.DeletionRequestedAt <= cutoff && !failed.Contains(user.Id));

            if (onlyUsers is not null)
            {
                due = due.Where(user => onlyUsers.Contains(user.Id));
            }

            var batch = await due.OrderBy(user => user.DeletionRequestedAt).ThenBy(user => user.Id).Select(user => user.Id).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return new AccountDeletionSummary(deleted, failed.Count, objects);
            }

            foreach (var userId in batch)
            {
                var result = await purger.PurgeAsync(userId, cancellationToken);

                switch (result.Status)
                {
                    case PurgeStatus.Purged:
                        deleted++;
                        objects += result.ObjectsDeleted;
                        break;
                    case PurgeStatus.StorageFailed:
                        failed.Add(userId);
                        break;
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Account deletion: {Accounts} account(s) deleted with {Objects} stored object(s); {Failures} postponed because the storage failed.")]
    private static partial void LogRun(ILogger logger, int accounts, int objects, int failures);
}
