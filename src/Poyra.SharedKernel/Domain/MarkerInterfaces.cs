namespace Poyra.SharedKernel.Domain;

public interface ITenantOwned
{
    Guid TenantId { get; }
}

public interface IHasCreatedAt
{
    DateTimeOffset CreatedAt { get; set; }
}

public interface IAuditable : IHasCreatedAt
{
    DateTimeOffset UpdatedAt { get; set; }
}
