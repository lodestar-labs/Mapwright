# Mapwright

**The compile-time object mapper. Declare the mapping; the compiler writes it, checks it, and shows you the code.**

[![CI](https://github.com/lodestar-labs/Mapwright/actions/workflows/ci.yml/badge.svg)](https://github.com/lodestar-labs/Mapwright/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

AutoMapper resolves mappings at runtime, by reflection and convention: a renamed property
maps to nothing, silently, until `AssertConfigurationIsValid()` fails in a test — or
nobody notices at all. Mapwright inverts the model. You declare each mapping as a
`static partial` method; a Roslyn incremental source generator writes the implementation
as **plain C# you can read, diff, and step through**; and every destination property that
nothing maps is a **compiler diagnostic**, not a runtime surprise.

```csharp
[Mapper]
public static partial class CatalogMapper
{
    // Domain record -> EF entity. Audit fields are the database's business.
    [MapIgnore(nameof(CodeEntity.User), nameof(CodeEntity.Created), nameof(CodeEntity.Modified))]
    public static partial CodeEntity ToEntity(Code source);

    // EF entity -> domain record. bool? columns collapse to non-nullable domain booleans.
    public static partial Code ToDomain(CodeEntity source);

    // In-place scalar copy onto the EF-tracked instance — the Repository.Update pattern.
    [MapIgnore(nameof(CodeEntity.User), nameof(CodeEntity.Created), nameof(CodeEntity.Modified))]
    public static partial void CopyScalars(CodeEntity source, CodeEntity target);

    // The ProjectTo replacement: an expression EF Core translates to SQL.
    public static partial Expression<Func<CodeEntity, CodeSummary>> SummaryProjection();

    // Collection overload, wired to the ToEntity map above.
    public static partial List<CodeEntity> ToEntities(IEnumerable<Code> source);
}
```

That is the entire mapping layer. No profiles, no `MapperConfiguration`, no `IMapper` to
inject, nothing to register in DI. Call `CatalogMapper.ToEntity(code)` like the ordinary
method it is.

## Why compile-time

- **Verification is the build.** AutoMapper's `AssertConfigurationIsValid()` runs when a
  test remembers to call it. Mapwright's equivalent (`MW0001`) runs on every keystroke,
  in the IDE, before the code ever executes — and stale configuration is an *error*:
  `[MapIgnore("RemovedLastSprint")]` referring to a property that no longer exists fails
  the build (`MW0003`), so ignore lists cannot rot.
- **The mapping is code you own.** Generated implementations land in ordinary `.g.cs`
  files: object initializers, `GetValueOrDefault()` where a nullable column collapses,
  a null-guard before a nested map. Breakpoints work. `git diff` of behavior is possible.
  There is no runtime black box to reverse-engineer at 2 a.m.
- **Nothing happens at runtime.** The `Mapwright` package is attributes only. No
  reflection, no expression compilation, no cache warm-up — which also means Native AOT
  and trimming just work, and the only allocation is the destination object itself.
- **EF Core projections without the magic.** A `Expression<Func<TSource, TDest>>`
  partial method generates the whole projection shape — nested maps inlined as
  initializers — so `queryable.Select(CatalogMapper.SummaryProjection())` translates to
  SQL exactly like a hand-written projection, because it *is* one.

## Install

Two packages: the attributes, and the generator that does the work.

```xml
<ItemGroup>
  <PackageReference Include="Mapwright" Version="0.1.0" />
  <PackageReference Include="Mapwright.Generator" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>
```

> Building from source instead: reference `src/Mapwright/Mapwright.csproj` normally and
> `src/Mapwright.Generator/Mapwright.Generator.csproj` with
> `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`.

## The mapping shapes

| Declare | Get |
| --- | --- |
| `static partial TDest Name(TSource s)` | Object map: initializer, null-guard, `ArgumentNullException` on null source. |
| `static partial void Name(TSource s, TDest t)` | In-place copy for EF-tracked instances; init-only members are flagged (`MW0006`), never half-copied. |
| `static partial Expression<Func<TSource, TDest>> Name()` | EF-translatable projection; nested maps inlined, cycles rejected (`MW0007`). |
| `static partial List<TDest> Name(IEnumerable<TSource> s)` | Collection map delegating to the sibling object map (`MW0008` if missing). Arrays too. |

**Matching**: case-insensitive by name (`CodeID → CodeId` needs nothing), `[MapProperty]`
for real renames, inherited members included, `T? → T` collapses via visible
`GetValueOrDefault()`, nested objects and collections route through sibling maps.

**Escape hatch**: `[AfterMap(nameof(Method))]` runs your hand-written fix-up after the
generated assignments — for computed values and everything a generator shouldn't guess.

## The diagnostics

| Id | Severity | Meaning |
| --- | --- | --- |
| `MW0001` | Warning | Destination property is not mapped — map it, `[MapIgnore]` it, or set it in AfterMap. |
| `MW0002` | Info | Source property is never read — `[MapIgnoreSource]` documents one-way fields. |
| `MW0003` | Error | Ignore/rename names a property that does not exist (configuration rot). |
| `MW0004` | Error | No conversion between the matched property types. |
| `MW0005` | Error | Method signature is not a recognized mapping shape. |
| `MW0006` | Warning | Init-only property cannot be set by an in-place copy. |
| `MW0007` | Error | Projection would recurse forever. |
| `MW0008` | Error | Collection map lacks its element map. |
| `MW0009` | Error | AfterMap target is missing or has the wrong signature. |

Promote `MW0001` to an error in `.editorconfig` when you want AutoMapper-strictness:

```ini
dotnet_diagnostic.MW0001.severity = error
```

## Replacing AutoMapper

**[docs/automapper-vs-mapwright.html](docs/automapper-vs-mapwright.html)** — the side-by-side
migration reference: why AutoMapper exists, the issues it leaves open, how Mapwright closes each
with modern C#, a statement-by-statement replacement guide, and a **runnable sample app whose real
output is printed in the doc** (`samples/CatalogApi.Sample` — `dotnet run --project samples/CatalogApi.Sample`).

**[docs/replacing-automapper.html](docs/replacing-automapper.html)** is the full
walkthrough, built on a real production case study: a catalog API whose AutoMapper
`MapperFactory` (entity↔domain maps, self-maps for tracked updates, `ForMember(...Ignore())`
lists) became three Mapwright declarations per entity — with the runtime
`AssertConfigurationIsValid()` test replaced by the compiler.

**[docs/automapper-and-mapwright-explained.html](docs/automapper-and-mapwright-explained.html)** —
a from-zero explanation of what AutoMapper is and how source-generated mapping replaces it.

## Honest limitations (v0.1)

- Destinations need an accessible parameterless constructor (records with `init`
  properties are perfect; constructor mapping is on the roadmap).
- Mapping methods are `static partial` on non-generic, top-level classes.
- No DI-resolved value converters inside maps — that's what `[AfterMap]` and ordinary
  methods are for.
- Enum-to-enum maps beyond identity, and `IQueryable` extension sugar, are roadmap items.

## Project layout

| Project | What it is |
| --- | --- |
| `src/Mapwright` | The attributes. The entire runtime surface — nothing executes. |
| `src/Mapwright.Generator` | The Roslyn incremental generator and its diagnostics. |
| `tests/Mapwright.Tests` | Behavior tests over real generated mappers + diagnostic tests driving the generator in-memory. |

## License

MIT — see [LICENSE](LICENSE).
