using System.Linq.Expressions;

namespace Mapwright.Tests;

[TestFixture]
public class MappingBehaviorTests
{
    private static Code SampleCode() => new()
    {
        CodeID = 7,
        CodeTypeID = 3,
        Value = "COD",
        Description = "Atlantic cod",
        Visibility = true,
        Deprecated = false,
        LongDescription = "Gadus morhua",
        Created = new DateTime(2026, 1, 1),
        Modified = new DateTime(2026, 6, 1),
    };

    [Test]
    public void Object_map_renames_by_casing_and_honors_ignores()
    {
        var entity = CatalogMapper.ToEntity(SampleCode());

        Assert.Multiple(() =>
        {
            Assert.That(entity.CodeId, Is.EqualTo(7), "CodeID (domain) maps to CodeId (entity) with no configuration");
            Assert.That(entity.CodeTypeId, Is.EqualTo(3));
            Assert.That(entity.Value, Is.EqualTo("COD"));
            Assert.That(entity.Visibility, Is.True, "bool lifts to bool? implicitly");
            Assert.That(entity.User, Is.Null, "ignored audit field stays untouched");
            Assert.That(entity.Created, Is.Null, "ignored audit field stays untouched");
            Assert.That(entity.ParentCodes, Is.Null, "ignored relationship baggage stays untouched");
        });
    }

    [Test]
    public void Object_map_unwraps_nullable_database_columns()
    {
        var domain = CatalogMapper.ToDomain(new CodeEntity
        {
            CodeId = 7,
            CodeTypeId = 3,
            Value = "COD",
            Visibility = null,
            Deprecated = true,
            Created = new DateTime(2026, 1, 1),
        });

        Assert.Multiple(() =>
        {
            Assert.That(domain.CodeID, Is.EqualTo(7));
            Assert.That(domain.Visibility, Is.False, "null database bool collapses to false, visibly via GetValueOrDefault");
            Assert.That(domain.Deprecated, Is.True);
            Assert.That(domain.Created, Is.EqualTo(new DateTime(2026, 1, 1)), "inherited record members map too");
        });
    }

    [Test]
    public void Object_map_throws_on_null_source()
    {
        Assert.Throws<ArgumentNullException>(() => CatalogMapper.ToEntity(null!));
    }

    [Test]
    public void Copy_updates_tracked_instance_in_place_preserving_ignored_fields()
    {
        var tracked = new CodeEntity
        {
            CodeId = 7,
            Value = "OLD",
            User = "importer",
            Created = new DateTime(2020, 1, 1),
            ParentCodes = ["kept"],
        };
        var incoming = new CodeEntity { CodeId = 7, Value = "NEW", Description = "fresh" };

        CatalogMapper.CopyScalars(incoming, tracked);

        Assert.Multiple(() =>
        {
            Assert.That(tracked.Value, Is.EqualTo("NEW"));
            Assert.That(tracked.Description, Is.EqualTo("fresh"));
            Assert.That(tracked.User, Is.EqualTo("importer"), "ignored: EF's audit column survives the update");
            Assert.That(tracked.Created, Is.EqualTo(new DateTime(2020, 1, 1)));
            Assert.That(tracked.ParentCodes, Is.SameAs(incoming.ParentCodes) | Is.Null,
                "identity-typed collections copy by reference, like the hand-written original");
        });
    }

    [Test]
    public void Collection_map_uses_the_sibling_object_map()
    {
        var entities = CatalogMapper.ToEntities([SampleCode(), SampleCode() with { CodeID = 8 }]);

        Assert.Multiple(() =>
        {
            Assert.That(entities, Has.Count.EqualTo(2));
            Assert.That(entities[1].CodeId, Is.EqualTo(8));
        });
    }

    [Test]
    public void Projection_is_a_translatable_member_init_expression()
    {
        var projection = CatalogMapper.SummaryProjection();

        Assert.That(projection.Body, Is.InstanceOf<MemberInitExpression>(),
            "EF Core requires a transparent initializer shape to translate to SQL");

        var summaries = new[]
        {
            new CodeEntity { CodeId = 1, Value = "COD", Description = "cod", Deprecated = false },
            new CodeEntity { CodeId = 2, Value = "HER", Description = "herring", Deprecated = true },
        }.AsQueryable().Select(projection).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(summaries, Has.Count.EqualTo(2));
            Assert.That(summaries[0].Value, Is.EqualTo("COD"));
            Assert.That(summaries[1].Deprecated, Is.True);
        });
    }

    [Test]
    public void Nested_objects_and_collections_map_through_sibling_maps()
    {
        var order = new OrderEntity
        {
            OrderId = 1,
            Number = "A-1",
            Customer = new CustomerEntity { CustomerId = 9, Name = "ICES" },
            Lines =
            [
                new LineEntity { LineId = 1, Sku = "X", Quantity = 2 },
                new LineEntity { LineId = 2, Sku = "Y", Quantity = 1 },
            ],
        };

        var dto = OrderMapper.ToDto(order);

        Assert.Multiple(() =>
        {
            Assert.That(dto.Customer!.Name, Is.EqualTo("ICES"));
            Assert.That(dto.Lines, Has.Count.EqualTo(2));
            Assert.That(dto.Lines![1].Sku, Is.EqualTo("Y"));
        });
    }

    [Test]
    public void Nested_object_map_handles_null_navigations()
    {
        var dto = OrderMapper.ToDto(new OrderEntity { OrderId = 1, Number = "A-1", Lines = [] });

        Assert.That(dto.Customer, Is.Null);
    }

    [Test]
    public void Projection_inlines_nested_maps_for_the_whole_aggregate()
    {
        var projection = OrderMapper.DtoProjection();
        var order = new OrderEntity
        {
            OrderId = 1,
            Number = "A-1",
            Customer = new CustomerEntity { CustomerId = 9, Name = "ICES" },
            Lines = [new LineEntity { LineId = 1, Sku = "X", Quantity = 2 }],
        };

        var dto = new[] { order }.AsQueryable().Select(projection).Single();

        Assert.Multiple(() =>
        {
            Assert.That(projection.Body, Is.InstanceOf<MemberInitExpression>());
            Assert.That(dto.Customer!.CustomerId, Is.EqualTo(9));
            Assert.That(dto.Lines!.Single().Sku, Is.EqualTo("X"));
        });
    }

    [Test]
    public void AfterMap_owns_the_hand_written_remainder()
    {
        var entity = ProvisioningMapper.ToNewEntity(SampleCode());

        Assert.Multiple(() =>
        {
            Assert.That(entity.User, Is.EqualTo("provisioning"), "set by the AfterMap, not the generator");
            Assert.That(entity.Created, Is.Not.Null);
            Assert.That(entity.CodeId, Is.EqualTo(7), "generated assignments ran first");
        });
    }
}
