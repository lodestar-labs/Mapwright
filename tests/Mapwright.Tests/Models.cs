namespace Mapwright.Tests;

// The domain/persistence pair mirrors the shape of a real production catalog API that
// migrated off AutoMapper: an immutable domain record (IDs suffixed "ID", non-nullable
// booleans) mapped against a mutable EF entity (IDs suffixed "Id", nullable database
// columns, audit fields, navigation baggage).

public abstract record AuditedRecord
{
    public DateTime? Created { get; init; }

    public DateTime? Modified { get; init; }
}

/// <summary>The immutable domain shape.</summary>
public sealed record Code : AuditedRecord
{
    public int CodeID { get; init; }

    public int CodeTypeID { get; init; }

    public string? Value { get; init; }

    public string? Description { get; init; }

    public bool Visibility { get; init; }

    public bool Deprecated { get; init; }

    public string? LongDescription { get; init; }
}

/// <summary>The mutable EF-style entity.</summary>
public sealed class CodeEntity
{
    public int CodeId { get; set; }

    public int CodeTypeId { get; set; }

    public string? Value { get; set; }

    public string? Description { get; set; }

    public string? User { get; set; }

    public DateTime? Created { get; set; }

    public DateTime? Modified { get; set; }

    public bool? Visibility { get; set; }

    public bool? Deprecated { get; set; }

    public string? LongDescription { get; set; }

    public List<string>? ParentCodes { get; set; }
}

/// <summary>A read-model shape for list endpoints, projected server-side.</summary>
public sealed record CodeSummary
{
    public int CodeId { get; init; }

    public string? Value { get; init; }

    public string? Description { get; init; }

    public bool? Deprecated { get; init; }
}

// A small aggregate exercising nested maps and collections of mapped elements.

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

    public string? Sku { get; set; }

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

    public string? Sku { get; init; }

    public int Quantity { get; init; }
}
