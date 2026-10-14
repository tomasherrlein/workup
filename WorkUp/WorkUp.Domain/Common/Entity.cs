namespace WorkUp.Domain.Common;

/// <summary>Base class for every persisted entity. Ids are SQL Server identity values.</summary>
public abstract class Entity
{
    public int Id { get; set; }
}

/// <summary>An entity that belongs to exactly one business (tenant).</summary>
public abstract class BusinessEntity : Entity
{
    public int BusinessId { get; set; }
}
