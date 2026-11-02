using Kombin.Api.Persistence;
using Kombin.Api.Persistence.Entities.Identity;
using Kombin.Api.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace Kombin.Api.Tests;

public class AuditInterceptorTests
{
    private static readonly DateTimeOffset Created = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Created.AddHours(5);

    private readonly FixedClock _clock = new(Created);

    // Change tracking never opens a connection, so the provider is only needed to build the model.
    private static KombinDbContext CreateContext() => new(
        new DbContextOptionsBuilder<KombinDbContext>()
            .UseNpgsql("Host=localhost;Database=never-connected", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void AddedEntitiesGetCreatedAndUpdatedFromTheClock()
    {
        using var context = CreateContext();
        var user = new User { Email = "ada@example.com", DisplayName = "Ada" };
        context.Users.Add(user);

        new AuditInterceptor(_clock).Apply(context);

        Assert.Equal(Created, user.CreatedAt);
        Assert.Equal(Created, user.UpdatedAt);
    }

    [Fact]
    public void ModifiedEntitiesOnlyBumpUpdatedAt()
    {
        using var context = CreateContext();
        var user = new User { Email = "ada@example.com", DisplayName = "Ada", CreatedAt = Created, UpdatedAt = Created };
        context.Users.Attach(user);
        user.DisplayName = "Ada Lovelace";
        _clock.UtcNow = Later;

        new AuditInterceptor(_clock).Apply(context);

        var entry = context.Entry(user);
        Assert.Equal(Later, user.UpdatedAt);
        Assert.Equal(Created, user.CreatedAt);
        Assert.False(entry.Property(tracked => tracked.CreatedAt).IsModified);
        Assert.True(entry.Property(tracked => tracked.UpdatedAt).IsModified);
    }

    [Fact]
    public void UnchangedEntitiesAreLeftAlone()
    {
        using var context = CreateContext();
        var user = new User { Email = "ada@example.com", DisplayName = "Ada", CreatedAt = Created, UpdatedAt = Created };
        context.Users.Attach(user);
        _clock.UtcNow = Later;

        new AuditInterceptor(_clock).Apply(context);

        Assert.Equal(Created, user.UpdatedAt);
    }

    [Fact]
    public void NullContextIsIgnored()
    {
        new AuditInterceptor(_clock).Apply(null);
    }
}
