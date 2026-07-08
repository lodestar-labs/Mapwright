using System.Globalization;
using CatalogApi.Sample;

// A self-contained demonstration: it runs the generated mappers over real objects and
// prints the results, so you can see Mapwright works without deploying anything. Run it
// with `dotnet run --project samples/CatalogApi.Sample`.

var ci = CultureInfo.InvariantCulture;
void Rule(string title) => Console.WriteLine($"\n=== {title} ===");

Console.WriteLine("Mapwright sample — every mapping below is generated C#, verified by the compiler.");

// 1) Entity -> domain. Note the nullable bool? column collapses to a non-nullable domain bool.
Rule("1. Entity -> domain (ToDomain)");
var entity = new ProductEntity
{
    ProductId = 42,
    CategoryId = 7,
    Name = "Widget",
    Price = 19.95m,
    IsActive = true,          // bool?  ->  bool
    User = "dbo",             // audit — deliberately not read (MapIgnoreSource)
    Created = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    Tags = ["clearance"],     // navigation baggage — not read
};
Product domain = CatalogMapper.ToDomain(entity);
Console.WriteLine($"  ProductID={domain.ProductID}  Name={domain.Name}  Price=${domain.Price.ToString("0.00", ci)}  IsActive={domain.IsActive}  Created={domain.Created:yyyy-MM-dd}");

// 2) Domain -> entity. Audit fields and Tags are left null (declared [MapIgnore]).
Rule("2. Domain -> entity (ToEntity)");
var newDomain = new Product { ProductID = 100, CategoryID = 3, Name = "Gadget", Price = 5.50m, IsActive = true };
ProductEntity mapped = CatalogMapper.ToEntity(newDomain);
Console.WriteLine($"  ProductId={mapped.ProductId}  Name={mapped.Name}  IsActive={mapped.IsActive}  User={(mapped.User ?? "<null>")}  Created={(mapped.Created?.ToString("yyyy-MM-dd") ?? "<null>")}");

// 3) In-place scalar copy onto an EF-tracked instance (Repository.Update). Audit fields on
//    the tracked entity are preserved because CopyScalars ignores them.
Rule("3. In-place update (CopyScalars) — audit fields preserved");
var tracked = new ProductEntity { ProductId = 42, Name = "Old name", Price = 9.99m, User = "auditor", Created = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
var incoming = new ProductEntity { ProductId = 42, Name = "New name", Price = 12.49m, IsActive = false };
CatalogMapper.CopyScalars(incoming, tracked);
Console.WriteLine($"  Name={tracked.Name}  Price=${tracked.Price.ToString("0.00", ci)}  (User still '{tracked.User}', Created still {tracked.Created:yyyy-MM-dd} — untouched)");

// 4) Collection map (ToEntities), delegating to the ToEntity object map.
Rule("4. Collection map (ToEntities)");
var many = new[]
{
    new Product { ProductID = 1, Name = "A", Price = 1m, IsActive = true },
    new Product { ProductID = 2, Name = "B", Price = 2m, IsActive = false },
};
List<ProductEntity> entities = CatalogMapper.ToEntities(many);
Console.WriteLine($"  mapped {entities.Count} entities: {string.Join(", ", entities.Select(e => $"{e.ProductId}:{e.Name}"))}");

// 5) EF-translatable projection (ProjectTo replacement). The expression shapes a SELECT.
Rule("5. Projection (SummaryProjection) applied in-memory here, EF-translatable in a query");
var proj = CatalogMapper.SummaryProjection().Compile();
var summaries = new[] { entity }.Select(proj).ToList();
Console.WriteLine($"  {summaries.Count} summary: ProductId={summaries[0].ProductId}  Name={summaries[0].Name}  Price=${summaries[0].Price.ToString("0.00", ci)}");

// 6) Nested object + collection + property rename (OrderEntity -> OrderDto).
Rule("6. Nested map + collection + rename (Order -> OrderDto)");
var order = new OrderEntity
{
    OrderId = 5001,
    Number = "ORD-5001",
    Customer = new CustomerEntity { CustomerId = 9, Name = "Acme Corp" },
    Lines =
    [
        new LineEntity { LineId = 1, Sku = "SKU-A", Quantity = 3 },
        new LineEntity { LineId = 2, Sku = "SKU-B", Quantity = 1 },
    ],
};
OrderDto dto = OrderMapper.ToDto(order);
Console.WriteLine($"  Order {dto.Number} for {dto.Customer?.Name}");
foreach (var line in dto.Lines ?? [])
    Console.WriteLine($"    line {line.LineId}: ProductCode={line.ProductCode} (from Sku), Qty={line.Quantity}");

// 7) AfterMap — the generated assignments run, then Stamp fills the fields the source can't supply.
Rule("7. AfterMap (ToNewEntity stamps audit fields)");
ProductEntity provisioned = ProvisioningMapper.ToNewEntity(newDomain);
Console.WriteLine($"  Name={provisioned.Name}  User={provisioned.User}  Created={provisioned.Created:yyyy-MM-dd}  (stamped by AfterMap)");

Console.WriteLine("\nAll mappings ran. Nothing was configured at startup, and no IMapper was injected.");
