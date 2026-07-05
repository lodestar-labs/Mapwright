namespace Mapwright;

/// <summary>
/// Marks a partial class whose unimplemented <c>static partial</c> methods are mapping
/// declarations for the Mapwright generator to implement. Three shapes are recognized:
/// <code>
/// [Mapper]
/// public static partial class CatalogMapper
/// {
///     public static partial CodeEntity ToEntity(Code source);                  // object map
///     public static partial void CopyScalars(CodeEntity source, CodeEntity target); // in-place copy
///     public static partial Expression&lt;Func&lt;Code, CodeEntity&gt;&gt; EntityProjection();  // EF-translatable projection
/// }
/// </code>
/// Collection overloads (<c>List&lt;TDest&gt; MapAll(IEnumerable&lt;TSource&gt;)</c>) are generated when a
/// matching single-object map exists in the same class. Every generated mapping is ordinary,
/// readable C# emitted at compile time; destination properties that nothing maps produce
/// compiler diagnostics rather than runtime surprises.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MapperAttribute : Attribute;
