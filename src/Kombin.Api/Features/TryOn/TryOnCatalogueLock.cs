using Kombin.Api.Persistence;
using Npgsql;

namespace Kombin.Api.Features.TryOn;

/// <summary>
/// At most one catalogue prepare at a time, whoever asks (the minute run, a request's targeted prepare, another instance): the try-on brief keeps
/// the catalogue backfill from queueing interactive vision calls (<c>/detect</c>, <c>/analyze</c>) behind it. A PostgreSQL session-level
/// advisory lock (<c>pg_try_advisory_lock(0x4B4F4D42, 4)</c>) on a dedicated, unpooled connection held for the catalogue part of a run, the
/// same pattern as the embedding run's lock (copied, not shared: two uses so far). It never waits: a caller that does not get it leaves the
/// catalogue to whoever holds it, and the server drops it if the process dies.
/// </summary>
internal sealed partial class TryOnCatalogueLock(IConfiguration configuration, ILogger<TryOnCatalogueLock> logger)
{
    /// <summary>"KOMB", shared by every Kombin lock of this kind.</summary>
    public const int ClassId = 0x4B4F4D42;

    /// <summary>The try-on catalogue (1 is the embedding run; 2 and 3 are the league's transaction locks).</summary>
    public const int ObjectId = 4;

    /// <summary>Takes the lock, or returns null at once when someone else holds it. Dispose the result to release it.</summary>
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

    private string ConnectionString()
    {
        var connectionString = configuration.GetConnectionString(PersistenceExtensions.ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string 'ConnectionStrings:{PersistenceExtensions.ConnectionStringName}' is not configured.");

        return new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, KeepAlive = 30, TcpKeepAlive = true, Enlist = false }.ConnectionString;
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
                // Closing the session below releases it anyway.
                LogUnlockFailed(logger, exception.Message);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Try-on catalogue lock: the explicit unlock failed ({Reason}); closing the connection releases it.")]
        private static partial void LogUnlockFailed(ILogger logger, string reason);
    }
}
