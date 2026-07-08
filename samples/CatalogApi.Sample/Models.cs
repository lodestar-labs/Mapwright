namespace CatalogApi.Sample;

// The classic two-model split every layered app has. The domain side is immutable and
// clean; the persistence side is mutable, has database-only audit columns, nullable
// columns, and navigation baggage. Mapping between them is what AutoMapper was for.

/// <summary>Audit fields the database owns — the domain never sets these.</summary>
public abstract record AuditedRecord
{
    public DateTime? Created { get; init; }
    public DateTime? Modified { get; init; }
}

/// <summary>The immutable domain shape (IDs suffixed <c>ID</c>, non-nullable booleans).</summary>
public sealed record Product : AuditedRecord
{
    public int ProductID { get; init; }
    public int CategoryID { get; init; }
    public string? Name { get; init; }
    public decimal Price { get; init; }
    public bool IsActive { get; init; }         // non-nullable in the domain
}

/// <summary>The mutable EF-style entity (IDs suffixed <c>Id</c>, nullable columns, audit + nav baggage).</summary>
public sealed class ProductEntity
{
    public int ProductId { get; set; }
    public int CategoryId { get; set; }
    public string? Name { get; set; }
    public decimal Price { get; set; }
    public bool? IsActive { get; set; }         // nullable database column
    public string? User { get; set; }           // audit — set by the persistence layer
    public DateTime? Created { get; set; }
    public DateTime? Modified { get; set; }
    public List<string>? Tags { get; set; }     // navigation baggage the domain doesn't carry
}

/// <summary>A read-model for list endpoints, projected server-side (the ProjectTo scenario).</summary>
public sealed record ProductSummary
{
    public int ProductId { get; init; }
    public string? Name { get; init; }
    public decimal Price { get; init; }
}

// A small aggregate for the nested-object + collection-of-mapped-elements + rename scenario.

public sealed class OrderEntity
{
    public int OrderId { get; set; }
    public string? Number { get; set; }
    public CustomerEntity? Customer { get; set; }
    public List<LineEntity>? Lines { get; set; }
}

public sealed class CustomerEntity
{
    public int CustomerId { get; set; }
    public string? Name { get; set; }
}

public sealed class LineEntity
{
    public int LineId { get; set; }
    public string? Sku { get; set; }            // renamed to ProductCode on the DTO
    public int Quantity { get; set; }
}

public sealed record OrderDto
{
    public int OrderId { get; init; }
    public string? Number { get; init; }
    public CustomerDto? Customer { get; init; }
    public List<LineDto>? Lines { get; init; }
}

public sealed record CustomerDto
{
    public int CustomerId { get; init; }
    public string? Name { get; init; }
}

public sealed record LineDto
{
    public int LineId { get; init; }
    public string? ProductCode { get; init; }   // sourced from LineEntity.Sku via [MapProperty]
    public int Quantity { get; init; }
}
