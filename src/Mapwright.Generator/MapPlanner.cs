using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Mapwright.Generator;

/// <summary>
/// All semantic analysis for one [Mapper] class: classifies each unimplemented partial
/// method into a mapping shape, matches properties, resolves conversions and nested maps,
/// and renders the resulting assignments as source text. Runs while symbols are in hand;
/// its output is the pure-value <see cref="MapperClassModel"/>.
/// </summary>
internal sealed class MapPlanner(INamedTypeSymbol mapperClass, Compilation compilation)
{
    private static readonly SymbolDisplayFormat Fq =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    private readonly List<PendingDiagnostic> _diagnostics = [];
    private readonly List<MapMethodModel> _methods = [];

    /// <summary>Object maps declared on the class, used to resolve nested and element maps.</summary>
    private readonly List<(ITypeSymbol Source, ITypeSymbol Dest, string Name)> _objectMaps = [];

    public MapperClassModel Plan()
    {
        var declarations = mapperClass.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.IsPartialDefinition && m.PartialImplementationPart is null)
            .ToArray();

        // First pass: register object maps so nested/element resolution sees them all,
        // regardless of declaration order.
        foreach (var method in declarations)
        {
            if (Classify(method) == MapKind.Object)
            {
                _objectMaps.Add((method.Parameters[0].Type, method.ReturnType, method.Name));
            }
        }

        foreach (var method in declarations)
        {
            PlanMethod(method);
        }

        return new MapperClassModel(
            mapperClass.ContainingNamespace.IsGlobalNamespace
                ? null
                : mapperClass.ContainingNamespace.ToDisplayString(),
            mapperClass.Name,
            $"{Accessibility(mapperClass.DeclaredAccessibility)} {(mapperClass.IsStatic ? "static " : "")}partial class {mapperClass.Name}",
            EquatableArray.From(_methods),
            EquatableArray.From(_diagnostics));
    }

    private void PlanMethod(IMethodSymbol method)
    {
        if (!method.IsStatic || method.IsGenericMethod || mapperClass.IsGenericType)
        {
            Report(Descriptors.UnsupportedSignature, method, method.Name,
                "mapping methods must be static and non-generic, on a non-generic top-level class");
            return;
        }

        var config = MapConfig.Read(method);
        switch (Classify(method))
        {
            case MapKind.Object:
                PlanObjectMap(method, config);
                break;
            case MapKind.Copy:
                PlanCopy(method, config);
                break;
            case MapKind.Projection:
                PlanProjection(method, config);
                break;
            case MapKind.Collection:
                PlanCollection(method, config);
                break;
            default:
                Report(Descriptors.UnsupportedSignature, method, method.Name,
                    "expected one of: TDest Name(TSource), void Name(TSource, TDest), " +
                    "Expression<Func<TSource, TDest>> Name(), or List<TDest>/TDest[] Name(IEnumerable<TSource>)");
                break;
        }
    }

    private MapKind? Classify(IMethodSymbol method)
    {
        if (method.ReturnsVoid && method.Parameters.Length == 2)
        {
            return MapKind.Copy;
        }

        if (method.Parameters.Length == 0 && IsExpressionOfFunc(method.ReturnType))
        {
            return MapKind.Projection;
        }

        if (method.Parameters.Length == 1 && !method.ReturnsVoid)
        {
            var returnElement = ElementTypeOf(method.ReturnType);
            var paramElement = ElementTypeOf(method.Parameters[0].Type);
            if (returnElement is not null && paramElement is not null)
            {
                return MapKind.Collection;
            }

            if (method.ReturnType is INamedTypeSymbol && method.Parameters[0].Type is INamedTypeSymbol)
            {
                return MapKind.Object;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ shapes

    private void PlanObjectMap(IMethodSymbol method, MapConfig config)
    {
        var source = method.Parameters[0].Type;
        var dest = method.ReturnType;
        var sourceName = method.Parameters[0].Name;

        var assignments = PlanAssignments(
            method, config, source, dest, sourceName,
            initializerSyntax: true, projection: false, []);

        _methods.Add(new MapMethodModel(
            MapKind.Object,
            Signature(method),
            sourceName,
            null,
            dest.ToDisplayString(Fq),
            source.IsValueType,
            EquatableArray.From(assignments),
            ValidateAfterMap(method, config, source, dest),
            null));
    }

    private void PlanCopy(IMethodSymbol method, MapConfig config)
    {
        var source = method.Parameters[0].Type;
        var dest = method.Parameters[1].Type;
        var sourceName = method.Parameters[0].Name;
        var targetName = method.Parameters[1].Name;

        var statements = new List<string>();
        foreach (var (destProp, expression) in PlanMemberPairs(method, config, source, dest, sourceName, projection: false, []))
        {
            if (destProp.SetMethod!.IsInitOnly)
            {
                Report(Descriptors.InitOnlyInCopy, method, dest.Name, destProp.Name, method.Name);
                continue;
            }

            // The emitter prefixes the target parameter, keeping the model emission-agnostic.
            statements.Add($"{destProp.Name} = {expression};");
        }

        _methods.Add(new MapMethodModel(
            MapKind.Copy,
            Signature(method),
            sourceName,
            targetName,
            dest.ToDisplayString(Fq),
            source.IsValueType,
            EquatableArray.From(statements),
            ValidateAfterMap(method, config, source, dest),
            null));
    }

    private void PlanProjection(IMethodSymbol method, MapConfig config)
    {
        var expression = (INamedTypeSymbol)method.ReturnType;
        var func = (INamedTypeSymbol)expression.TypeArguments[0];
        var source = func.TypeArguments[0];
        var dest = func.TypeArguments[1];

        if (config.AfterMap is not null)
        {
            Report(Descriptors.BadAfterMap, method, config.AfterMap, method.Name,
                "AfterMap cannot run inside an expression tree; projections must stay translatable to SQL");
        }

        var assignments = PlanAssignments(
            method, config, source, dest, "source",
            initializerSyntax: true, projection: true, []);

        _methods.Add(new MapMethodModel(
            MapKind.Projection,
            Signature(method),
            "source",
            null,
            dest.ToDisplayString(Fq),
            source.IsValueType,
            EquatableArray.From(assignments),
            null,
            null));
    }

    private void PlanCollection(IMethodSymbol method, MapConfig config)
    {
        var sourceElement = ElementTypeOf(method.Parameters[0].Type)!;
        var destElement = ElementTypeOf(method.ReturnType)!;
        var sourceName = method.Parameters[0].Name;

        string projectedItems;
        if (SymbolEqualityComparer.Default.Equals(sourceElement, destElement))
        {
            projectedItems = sourceName;
        }
        else if (FindObjectMap(sourceElement, destElement) is { } elementMap)
        {
            projectedItems = $"global::System.Linq.Enumerable.Select({sourceName}, {elementMap})";
        }
        else
        {
            Report(Descriptors.MissingElementMap, method, method.Name,
                sourceElement.ToDisplayString(), destElement.ToDisplayString());
            return;
        }

        var body = Materialize(method.ReturnType, projectedItems);
        _methods.Add(new MapMethodModel(
            MapKind.Collection,
            Signature(method),
            sourceName,
            null,
            method.ReturnType.ToDisplayString(Fq),
            method.Parameters[0].Type.IsValueType,
            EquatableArray.From(Array.Empty<string>()),
            null,
            body));
    }

    // ------------------------------------------------------- member planning

    private List<string> PlanAssignments(
        IMethodSymbol method,
        MapConfig config,
        ITypeSymbol source,
        ITypeSymbol dest,
        string sourceExpr,
        bool initializerSyntax,
        bool projection,
        Stack<(ITypeSymbol, ITypeSymbol)> inliningStack)
    {
        var result = new List<string>();
        foreach (var (destProp, expression) in PlanMemberPairs(method, config, source, dest, sourceExpr, projection, inliningStack))
        {
            result.Add(initializerSyntax
                ? $"{destProp.Name} = {expression}"
                : $"{destProp.Name} = {expression};");
        }

        return result;
    }

    /// <summary>
    /// The matching core shared by every shape: yields (destination property, rendered
    /// source expression) pairs and reports MW0001–MW0004 for everything that falls out.
    /// </summary>
    private IEnumerable<(IPropertySymbol Dest, string Expression)> PlanMemberPairs(
        IMethodSymbol method,
        MapConfig config,
        ITypeSymbol source,
        ITypeSymbol dest,
        string sourceExpr,
        bool projection,
        Stack<(ITypeSymbol, ITypeSymbol)> inliningStack)
    {
        var destProps = SettableProperties(dest);
        var sourceProps = ReadableProperties(source);

        // Configuration must reference real properties, or it rots as the models evolve.
        foreach (var name in config.IgnoredTargets.Where(n => !destProps.ContainsKey(n)))
        {
            Report(Descriptors.UnknownConfiguredProperty, method, name, dest.Name, "[MapIgnore]");
        }

        foreach (var name in config.IgnoredSources.Where(n => !sourceProps.ContainsKey(n)))
        {
            Report(Descriptors.UnknownConfiguredProperty, method, name, source.Name, "[MapIgnoreSource]");
        }

        foreach (var pair in config.Renames.Where(p => !sourceProps.ContainsKey(p.Source)))
        {
            Report(Descriptors.UnknownConfiguredProperty, method, pair.Source, source.Name, "[MapProperty]");
        }

        var matchedSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pairs = new List<(IPropertySymbol Dest, string Expression)>();
        foreach (var destProp in destProps.Values)
        {
            var rename = config.Renames.FirstOrDefault(p =>
                string.Equals(p.Target, destProp.Name, StringComparison.OrdinalIgnoreCase));
            var sourceProp = rename.Source is not null && sourceProps.TryGetValue(rename.Source, out var renamed)
                ? renamed
                : sourceProps.TryGetValue(destProp.Name, out var matched) ? matched : null;

            if (sourceProp is not null)
            {
                matchedSourceNames.Add(sourceProp.Name);
            }

            if (config.IgnoredTargets.Contains(destProp.Name))
            {
                continue;
            }

            if (sourceProp is null)
            {
                Report(Descriptors.UnmappedTargetProperty, method, destProp.Name, dest.Name, method.Name);
                continue;
            }

            var expression = RenderConversion(
                method, sourceProp, destProp, source, dest, $"{sourceExpr}.{sourceProp.Name}", projection, inliningStack);
            if (expression is not null)
            {
                pairs.Add((destProp, expression));
            }
        }

        foreach (var sourceProp in sourceProps.Values)
        {
            if (!matchedSourceNames.Contains(sourceProp.Name)
                && !config.IgnoredSources.Contains(sourceProp.Name)
                && !config.Renames.Any(p => string.Equals(p.Source, sourceProp.Name, StringComparison.OrdinalIgnoreCase)))
            {
                Report(Descriptors.UnusedSourceProperty, method, sourceProp.Name, source.Name, method.Name);
            }
        }

        return pairs;
    }

    /// <summary>Renders the source-side expression for one member, or null when no conversion exists.</summary>
    private string? RenderConversion(
        IMethodSymbol method,
        IPropertySymbol sourceProp,
        IPropertySymbol destProp,
        ITypeSymbol sourceType,
        ITypeSymbol destType,
        string valueExpr,
        bool projection,
        Stack<(ITypeSymbol, ITypeSymbol)> inliningStack)
    {
        var src = sourceProp.Type;
        var dst = destProp.Type;

        // T? -> T for value types: AutoMapper's null-to-default behavior, made visible.
        if (src is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullableSrc
            && SymbolEqualityComparer.Default.Equals(nullableSrc.TypeArguments[0], dst))
        {
            return $"{valueExpr}.GetValueOrDefault()";
        }

        var conversion = compilation.ClassifyCommonConversion(src, dst);
        if (conversion.IsIdentity || (conversion.IsImplicit && !conversion.IsUserDefined))
        {
            return NeedsNullForgiveness(src, dst) ? $"{valueExpr}!" : valueExpr;
        }

        // Nested single objects, via another object map on this class.
        if (FindObjectMap(src, dst) is { } nestedMap)
        {
            if (projection)
            {
                return InlineProjection(method, src, dst, valueExpr, inliningStack);
            }

            return src.IsValueType
                ? $"{nestedMap}({valueExpr})"
                : $"{valueExpr} is null ? null! : {nestedMap}({valueExpr})";
        }

        // Collections, when the element types are identical or element-mappable.
        if (ElementTypeOf(src) is { } srcElement && ElementTypeOf(dst) is { } dstElement)
        {
            // In projections there is no null-guard branch (EF evaluates server-side), so
            // the null-forgiving operator keeps the generated tree warning-free.
            var sequence = projection && src is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.Annotated }
                ? $"{valueExpr}!"
                : valueExpr;
            var items = SymbolEqualityComparer.Default.Equals(srcElement, dstElement)
                ? sequence
                : FindObjectMap(srcElement, dstElement) is { } elementMap
                    ? projection
                        ? InlineProjectedSelect(method, srcElement, dstElement, sequence, inliningStack)
                        : $"global::System.Linq.Enumerable.Select({sequence}, {elementMap})"
                    : null;
            if (items is null)
            {
                Report(Descriptors.NoConversion, method,
                    sourceType.Name, sourceProp.Name, src.ToDisplayString(),
                    destType.Name, destProp.Name, dst.ToDisplayString());
                return null;
            }

            var materialized = Materialize(dst, items);
            if (projection || src.IsValueType)
            {
                return materialized;
            }

            return $"{valueExpr} is null ? null! : {materialized}";
        }

        Report(Descriptors.NoConversion, method,
            sourceType.Name, sourceProp.Name, src.ToDisplayString(),
            destType.Name, destProp.Name, dst.ToDisplayString());
        return null;
    }

    /// <summary>
    /// Projections cannot call methods (EF must see the shape), so nested maps are inlined
    /// as nested object initializers, recursively, with a cycle guard.
    /// </summary>
    private string? InlineProjection(
        IMethodSymbol method,
        ITypeSymbol src,
        ITypeSymbol dst,
        string valueExpr,
        Stack<(ITypeSymbol, ITypeSymbol)> inliningStack)
    {
        if (inliningStack.Any(pair =>
                SymbolEqualityComparer.Default.Equals(pair.Item1, src)
                && SymbolEqualityComparer.Default.Equals(pair.Item2, dst)))
        {
            Report(Descriptors.CircularProjection, method, method.Name, dst.ToDisplayString());
            return null;
        }

        inliningStack.Push((src, dst));
        var nested = PlanAssignments(
            method, MapConfig.Empty, src, dst, valueExpr,
            initializerSyntax: true, projection: true, inliningStack);
        inliningStack.Pop();

        // 'new Foo?' is not legal syntax; the created type is always the bare type.
        var createdType = dst.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(Fq);
        var initializer = $"new {createdType} {{ {string.Join(", ", nested)} }}";
        return src.IsValueType
            ? initializer
            : $"{valueExpr} == null ? null! : {initializer}";
    }

    private string? InlineProjectedSelect(
        IMethodSymbol method,
        ITypeSymbol srcElement,
        ITypeSymbol dstElement,
        string valueExpr,
        Stack<(ITypeSymbol, ITypeSymbol)> inliningStack)
    {
        var item = InlineProjection(method, srcElement, dstElement, "item", inliningStack);
        return item is null
            ? null
            : $"global::System.Linq.Enumerable.Select({valueExpr}, item => {item})";
    }

    // ----------------------------------------------------------------- helpers

    private string? ValidateAfterMap(IMethodSymbol method, MapConfig config, ITypeSymbol source, ITypeSymbol dest)
    {
        if (config.AfterMap is null)
        {
            return null;
        }

        var candidate = mapperClass.GetMembers(config.AfterMap)
            .OfType<IMethodSymbol>()
            .FirstOrDefault(m => m is { IsStatic: true, ReturnsVoid: true, Parameters.Length: 2 }
                && compilation.ClassifyCommonConversion(source, m.Parameters[0].Type) is { IsIdentity: true } or { IsImplicit: true }
                && compilation.ClassifyCommonConversion(dest, m.Parameters[1].Type) is { IsIdentity: true } or { IsImplicit: true });

        if (candidate is null)
        {
            Report(Descriptors.BadAfterMap, method, config.AfterMap, method.Name,
                $"no static void {config.AfterMap}({source.Name} source, {dest.Name} result) method exists in the mapper class");
            return null;
        }

        return config.AfterMap;
    }

    private string? FindObjectMap(ITypeSymbol source, ITypeSymbol dest) =>
        _objectMaps.FirstOrDefault(m =>
            SymbolEqualityComparer.Default.Equals(m.Source, source)
            && SymbolEqualityComparer.Default.Equals(m.Dest, dest)).Name;

    private static Dictionary<string, IPropertySymbol> SettableProperties(ITypeSymbol type) =>
        Properties(type).Where(p => p.SetMethod is { DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Public })
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, IPropertySymbol> ReadableProperties(ITypeSymbol type) =>
        Properties(type).Where(p => p.GetMethod is { DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Public })
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Public instance properties, most-derived declaration winning, base types included.</summary>
    private static IEnumerable<IPropertySymbol> Properties(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>())
            {
                if (property is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Microsoft.CodeAnalysis.Accessibility.Public }
                    && property.Name != "EqualityContract"
                    && seen.Add(property.Name))
                {
                    yield return property;
                }
            }
        }
    }

    private static bool NeedsNullForgiveness(ITypeSymbol src, ITypeSymbol dst) =>
        src is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.Annotated }
        && dst is { IsReferenceType: true, NullableAnnotation: NullableAnnotation.NotAnnotated };

    private static bool IsExpressionOfFunc(ITypeSymbol type) =>
        type is INamedTypeSymbol
        {
            Name: "Expression",
            TypeArguments.Length: 1,
            ContainingNamespace:
            {
                Name: "Expressions",
                ContainingNamespace: { Name: "Linq", ContainingNamespace.Name: "System" },
            },
        } expression
        && expression.TypeArguments[0] is INamedTypeSymbol { Name: "Func", TypeArguments.Length: 2 };

    /// <summary>Element type when the type is a mappable collection; null otherwise. Strings are not collections.</summary>
    private static ITypeSymbol? ElementTypeOf(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { SpecialType: SpecialType.System_String } => null,
        INamedTypeSymbol { TypeArguments.Length: 1 } named when named.OriginalDefinition.ToDisplayString() is
            "System.Collections.Generic.List<T>"
            or "System.Collections.Generic.IList<T>"
            or "System.Collections.Generic.ICollection<T>"
            or "System.Collections.Generic.IEnumerable<T>"
            or "System.Collections.Generic.IReadOnlyList<T>"
            or "System.Collections.Generic.IReadOnlyCollection<T>" => named.TypeArguments[0],
        _ => null,
    };

    /// <summary>Wraps a projected item sequence in the concrete shape the destination expects.</summary>
    private static string Materialize(ITypeSymbol destination, string items) =>
        destination is IArrayTypeSymbol
            ? $"global::System.Linq.Enumerable.ToArray({items})"
            : $"global::System.Linq.Enumerable.ToList({items})";

    private string Signature(IMethodSymbol method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p => $"{p.Type.ToDisplayString(Fq)} {p.Name}"));
        return $"{Accessibility(method.DeclaredAccessibility)} static partial {method.ReturnType.ToDisplayString(Fq)} {method.Name}({parameters})";
    }

    private static string Accessibility(Microsoft.CodeAnalysis.Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        _ => "public",
    };

    private void Report(DiagnosticDescriptor descriptor, IMethodSymbol method, params string[] args) =>
        _diagnostics.Add(PendingDiagnostic.Create(descriptor, method.Locations[0], args));
}

/// <summary>Per-method configuration read from the Mapwright attributes.</summary>
internal sealed class MapConfig
{
    public static readonly MapConfig Empty = new();

    public HashSet<string> IgnoredTargets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> IgnoredSources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Source, string Target)> Renames { get; } = [];

    public string? AfterMap { get; private set; }

    public static MapConfig Read(IMethodSymbol method)
    {
        var config = new MapConfig();
        foreach (var attribute in method.GetAttributes())
        {
            if (attribute.AttributeClass is not { ContainingNamespace.Name: "Mapwright" } attributeClass)
            {
                continue;
            }

            switch (attributeClass.Name)
            {
                case "MapIgnoreAttribute":
                    config.IgnoredTargets.UnionWith(StringArgs(attribute));
                    break;
                case "MapIgnoreSourceAttribute":
                    config.IgnoredSources.UnionWith(StringArgs(attribute));
                    break;
                case "MapPropertyAttribute" when attribute.ConstructorArguments.Length == 2
                    && attribute.ConstructorArguments[0].Value is string source
                    && attribute.ConstructorArguments[1].Value is string target:
                    config.Renames.Add((source, target));
                    break;
                case "AfterMapAttribute" when attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string name:
                    config.AfterMap = name;
                    break;
            }
        }

        return config;
    }

    private static IEnumerable<string> StringArgs(AttributeData attribute) =>
        attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Values.Select(v => v.Value).OfType<string>()
            : [];
}
