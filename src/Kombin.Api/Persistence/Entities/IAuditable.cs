namespace Kombin.Api.Persistence.Entities;

/// <summary>Entities whose timestamps are maintained by <see cref="Interceptors.AuditInterceptor"/>.</summary>
internal interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset UpdatedAt { get; set; }
}
