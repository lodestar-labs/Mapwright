namespace Mapwright.Generator;

/// <summary>
/// Everything the emitter needs, reduced to plain equatable values. All semantic analysis
/// (name matching, conversion classification, nested-map resolution) happens while the
/// symbols are in hand; what survives into the pipeline is strings, so unchanged input
/// syntax reuses the cached model and the generator does no work at all.
/// </summary>
internal sealed record MapperClassModel(
    string? Namespace,
    string ClassName,
    string ClassDeclaration,
    EquatableArray<MapMethodModel> Methods,
    EquatableArray<PendingDiagnostic> Diagnostics)
{
    public string HintName => (Namespace is null ? ClassName : $"{Namespace}.{ClassName}") + ".Mapwright.g.cs";
}

internal sealed record MapMethodModel(
    MapKind Kind,
    string Signature,
    string SourceParameterName,
    string? TargetParameterName,
    string ConstructedTypeFq,
    bool SourceIsValueType,
    EquatableArray<string> Assignments,
    string? AfterMapName,
    string? CollectionBody);

internal enum MapKind
{
    Object,
    Copy,
    Projection,
    Collection,
}
