using System.Linq.Expressions;

namespace Mapwright.Tests;

/// <summary>
/// The catalog mapper, shaped exactly like the AutoMapper profile it replaces in the
/// case-study API: an entity↔domain pair with asymmetric ignore lists, a scalar self-copy
/// for EF-tracked updates, a server-side projection, and a collection overload.
/// </summary>
[Mapper]
public static partial class CatalogMapper
{
    // Domain -> entity. Audit fields and relationship baggage are the database's business:
    // the same properties the old profile listed as ForMember(..., opt => opt.Ignore()).
    [MapIgnore(nameof(CodeEntity.User), nameof(CodeEntity.Created), nameof(CodeEntity.Modified), nameof(CodeEntity.ParentCodes))]
    public static partial CodeEntity ToEntity(Code source);

    // Entity -> domain. bool? database columns collapse to non-nullable domain booleans.
    [MapIgnoreSource(nameof(CodeEntity.User), nameof(CodeEntity.ParentCodes))]
    public static partial Code ToDomain(CodeEntity source);

    // In-place scalar copy onto the EF-tracked instance — the Repository.Update pattern.
    [MapIgnore(nameof(CodeEntity.User), nameof(CodeEntity.Created), nameof(CodeEntity.Modified))]
    public static partial void CopyScalars(CodeEntity source, CodeEntity target);

    // The ProjectTo replacement: an expression EF Core translates to SQL.
    public static partial Expression<Func<CodeEntity, CodeSummary>> SummaryProjection();

    public static partial List<CodeEntity> ToEntities(IEnumerable<Code> source);
}

/// <summary>Nested objects, collections of mapped elements, and an AfterMap fix-up.</summary>
[Mapper]
public static partial class OrderMapper
{
    public static partial CustomerDto ToDto(CustomerEntity source);

    public static partial LineDto ToDto(LineEntity source);

    public static partial OrderDto ToDto(OrderEntity source);

    // Projections inline nested maps as initializers, so EF sees the whole shape.
    public static partial Expression<Func<OrderEntity, OrderDto>> DtoProjection();
}

/// <summary>An [AfterMap] hook owning the hand-written remainder of a mapping.</summary>
[Mapper]
public static partial class ProvisioningMapper
{
    // User and Created are deliberately ignored by the generated assignments and then
    // stamped by the AfterMap — the pattern for values the source cannot supply.
    [MapIgnore(nameof(CodeEntity.User), nameof(CodeEntity.Created), nameof(CodeEntity.Modified), nameof(CodeEntity.ParentCodes))]
    [AfterMap(nameof(Stamp))]
    public static partial CodeEntity ToNewEntity(Code source);

    private static void Stamp(Code source, CodeEntity result)
    {
        result.User = "provisioning";
        result.Created = DateTime.UtcNow;
    }
}
