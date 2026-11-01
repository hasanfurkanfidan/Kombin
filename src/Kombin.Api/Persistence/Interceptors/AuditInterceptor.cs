using Kombin.Api.Common.Time;
using Kombin.Api.Persistence.Entities;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Kombin.Api.Persistence.Interceptors;

/// <summary>
/// Sets <see cref="IAuditable.CreatedAt"/> and <see cref="IAuditable.UpdatedAt"/> from <see cref="IClock"/> on save.
/// Bulk operations such as <c>ExecuteUpdateAsync</c> bypass it and must set <c>UpdatedAt</c> themselves.
/// </summary>
internal sealed class AuditInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    internal void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(auditable => auditable.CreatedAt).IsModified = false;
                    break;
            }
        }
    }
}
