using Kombin.Api.Persistence;
using Npgsql;

namespace Kombin.Api.Features.Catalog.Embeddings;

/// <summary>
/// One embedding run at a time, whoever starts it (the nightly job, the admin panel's backfill, <c>POST /internal/catalog/embeddings/run</c>).
/// A run can last hours at one request per host per second, and every Hangfire protection against overlap is bounded: its invisibility
/// timeout lets a second worker fetch a job that is still running, and its distributed lock has no heartbeat, so it counts as abandoned after
/// ten minutes. This is a PostgreSQL session-level advisory lock (<c>pg_try_advisory_lock</c>) on a dedicated connection that stays open for
/// the whole run: nothing to expire, it ends with <see cref="IAsyncDisposable.DisposeAsync"/> and the server drops it by itself if the process
/// (or just that connection) dies. It never waits: a caller that does not get it is told so and does nothing.
/// </summary>
/// <remarks>
/// The connection is not pooled, so closing it really ends the session. The two-int key (<see cref="ClassId"/>, <see cref="ObjectId"/>) never
/// meets the single-bigint keys or the transaction-scoped locks used elsewhere. Known limit: if only the network path to the database breaks,
/// the server drops the lock when it notices the dead connection (TCP keep-alive is on) and the running process does not find out; a second
/// run could then start. That takes a database outage in the middle of a run, and the work is resumable and idempotent anyway.
/// </remarks>
internal sealed partial class EmbeddingRunLock(IConfiguration configuration, ILogger<EmbeddingRunLock> logger)
{
    /// <summary>"KOMB": the first half of the advisory key, shared by every Kombin lock of this kind.</summary>
    public const int ClassId = 0x4B4F4D42;

    /// <summary>The second half: the embedding run.</summary>
    public const int ObjectId = 1;

    /// <summary>Takes the lock, or returns null at once when another run holds it. Dispose the result to release it.</summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(ConnectionString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@class, @object)", connection);
            command.Parameters.AddWithValue("class", ClassId);
            command.Parameters.AddWithValue("object", ObjectId);

            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
            {
                return new Held(connection, logger);
            }
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        await connection.DisposeAsync();

        return null;
    }

    /// <summary>Whether some session holds the lock right now (a look, not a reservation: the admin panel uses it to say "a run is in progress").</summary>
    public async Task<bool> IsHeldAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND granted AND classid = @class AND objid = @object AND objsubid = 2)", connection);
        command.Parameters.AddWithValue("class", ClassId);
        command.Parameters.AddWithValue("object", ObjectId);

        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private string ConnectionString()
    {
        var connectionString = configuration.GetConnectionString(PersistenceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string 'ConnectionStrings:{PersistenceExtensions.ConnectionStringName}' is not configured.");

        return new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            // The connection sits idle for hours while the run works on others: keep it alive so a middlebox does not quietly drop it.
            KeepAlive = 30,
            TcpKeepAlive = true,
            Enlist = false,
        }.ConnectionString;
    }

    private sealed partial class Held(NpgsqlConnection connection, ILogger logger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@class, @object)", connection);
                command.Parameters.AddWithValue("class", ClassId);
                command.Parameters.AddWithValue("object", ObjectId);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or IOException)
            {
                // The session is closed right below, and the server releases a session's locks when it ends.
                LogUnlockFailed(logger, exception.Message);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Embedding run lock: the explicit unlock failed ({Reason}); closing the connection releases it.")]
        private static partial void LogUnlockFailed(ILogger logger, string reason);
    }
}
