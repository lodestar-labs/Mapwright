using System.Linq.Expressions;
using Mapwright;

namespace CatalogApi.Sample;

/// <summary>
/// The entire mapping layer for the catalog, declared once. Every method is a bodyless
/// <c>static partial</c>; the Mapwright source generator writes the implementations at
/// compile time as ordinary, readable C#. No profiles, no <c>MapperConfiguration</c>,
/// no <c>IMapper</c> to inject, nothing to register in DI.
/// </summary>
[Mapper]
public static partial class CatalogMapper
{
    // Domain -> entity. Audit fields and navigation baggage are the database's business,
    // so they're declared ignored (the old AutoMapper ForMember(..., o => o.Ignore()) list).
    // Naming a property that no longer exists here would be a BUILD ERROR (MW0003).
    [MapIgnore(nameof(ProductEntity.User), nameof(ProductEntity.Created),
               nameof(ProductEntity.Modified), nameof(ProductEntity.Tags))]
    public static partial ProductEntity ToEntity(Product source);

    // Entity -> domain. The nullable bool? column collapses to the domain's non-nullable
    // bool automatically (GetValueOrDefault() in the generated code, visibly).
    [MapIgnoreSource(nameof(ProductEntity.User), nameof(ProductEntity.Tags))]
    public static partial Product ToDomain(ProductEntity source);

    // In-place scalar copy onto an EF-tracked instance — the Repository.Update pattern
    // (AutoMapper's _mapper.Map(source, tracked) self-map).
    [MapIgnore(nameof(ProductEntity.User), nameof(ProductEntity.Created), nameof(ProductEntity.Modified))]
    public static partial void CopyScalars(ProductEntity source, ProductEntity target);

    // The ProjectTo replacement: an Expression EF Core translates to SQL.
    public static partial Expression<Func<ProductEntity, ProductSummary>> SummaryProjection();

    // Collection overload, wired to the ToEntity object map above.
    public static partial List<ProductEntity> ToEntities(IEnumerable<Product> source);
}

/// <summary>Nested objects, a collection of mapped elements, and a property rename.</summary>
[Mapper]
public static partial class OrderMapper
{
    public static partial CustomerDto ToDto(CustomerEntity source);

    // LineEntity.Sku is named ProductCode on the DTO — the one real rename, declared once.
    [MapProperty(nameof(LineEntity.Sku), nameof(LineDto.ProductCode))]
    public static partial LineDto ToDto(LineEntity source);

    // Order maps its nested Customer and its Lines collection through the sibling maps above.
    public static partial OrderDto ToDto(OrderEntity source);
}

/// <summary>An [AfterMap] hook owning the small hand-written remainder of a mapping.</summary>
[Mapper]
public static partial class ProvisioningMapper
{
    // User/Created are ignored by the generated assignments, then stamped by the AfterMap —
    // the pattern for values the source cannot supply.
    [MapIgnore(nameof(ProductEntity.User), nameof(ProductEntity.Created),
               nameof(ProductEntity.Modified), nameof(ProductEntity.Tags))]
    [AfterMap(nameof(Stamp))]
    public static partial ProductEntity ToNewEntity(Product source);

    private static void Stamp(Product source, ProductEntity result)
    {
        result.User = "provisioning";
        result.Created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }
}
