using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Mapwright.Generator;

internal static class Descriptors
{
    private const string Category = "Mapwright";

    private const string HelpBase = "https://github.com/KadjiProjects/Mapwright#";

    public static readonly DiagnosticDescriptor UnmappedTargetProperty = new(
        "MW0001",
        "Destination property is not mapped",
        "Destination property '{0}' on '{1}' is not mapped by '{2}'. Map it, add it to [MapIgnore], or set it in an [AfterMap] method.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Every destination property must be mapped, ignored, or handled in AfterMap. This is Mapwright's compile-time replacement for AutoMapper's AssertConfigurationIsValid().",
        helpLinkUri: HelpBase + "mw0001");

    public static readonly DiagnosticDescriptor UnusedSourceProperty = new(
        "MW0002",
        "Source property is never read",
        "Source property '{0}' on '{1}' is not read by '{2}'. Add [MapIgnoreSource(nameof({1}.{0}))] to document that this is deliberate.",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0002");

    public static readonly DiagnosticDescriptor UnknownConfiguredProperty = new(
        "MW0003",
        "Mapping configuration names an unknown property",
        "'{0}' does not exist on '{1}' — the {2} list is out of date",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Ignore lists and rename pairs are verified against the models, so configuration cannot silently rot as types evolve.",
        helpLinkUri: HelpBase + "mw0003");

    public static readonly DiagnosticDescriptor NoConversion = new(
        "MW0004",
        "No conversion between mapped properties",
        "Cannot map '{0}.{1}' ({2}) to '{3}.{4}' ({5}): no implicit conversion, nested map, or collection mapping applies",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0004");

    public static readonly DiagnosticDescriptor UnsupportedSignature = new(
        "MW0005",
        "Unsupported mapping method signature",
        "'{0}' is not a recognized mapping shape: {1}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0005");

    public static readonly DiagnosticDescriptor InitOnlyInCopy = new(
        "MW0006",
        "Init-only property cannot be set by an in-place copy",
        "'{0}.{1}' is init-only, so '{2}' cannot assign it in place. Add it to [MapIgnore] or use an object map.",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0006");

    public static readonly DiagnosticDescriptor CircularProjection = new(
        "MW0007",
        "Projection would recurse forever",
        "Projection '{0}' cannot inline '{1}' because it recursively contains itself; project a flatter shape or ignore the cyclic property",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0007");

    public static readonly DiagnosticDescriptor MissingElementMap = new(
        "MW0008",
        "Collection map has no element map",
        "'{0}' maps collections of '{1}' to '{2}', but no object map with that exact signature exists in the class. Declare one: public static partial {2} Map({1} source).",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0008");

    public static readonly DiagnosticDescriptor BadAfterMap = new(
        "MW0009",
        "AfterMap method is invalid",
        "[AfterMap(\"{0}\")] on '{1}': {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        helpLinkUri: HelpBase + "mw0009");
}

/// <summary>
/// A diagnostic captured as pure values so the pipeline model stays equatable/cacheable;
/// rehydrated into a real <see cref="Diagnostic"/> only at report time.
/// </summary>
internal sealed record PendingDiagnostic(
    string Id,
    string FilePath,
    int SpanStart,
    int SpanLength,
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    EquatableArray<string> Arguments)
{
    public static PendingDiagnostic Create(DiagnosticDescriptor descriptor, Location location, params string[] args)
    {
        var span = location.GetLineSpan();
        return new PendingDiagnostic(
            descriptor.Id,
            span.Path,
            location.SourceSpan.Start,
            location.SourceSpan.Length,
            span.StartLinePosition.Line,
            span.StartLinePosition.Character,
            span.EndLinePosition.Line,
            span.EndLinePosition.Character,
            new EquatableArray<string>(args));
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = Id switch
        {
            "MW0001" => Descriptors.UnmappedTargetProperty,
            "MW0002" => Descriptors.UnusedSourceProperty,
            "MW0003" => Descriptors.UnknownConfiguredProperty,
            "MW0004" => Descriptors.NoConversion,
            "MW0005" => Descriptors.UnsupportedSignature,
            "MW0006" => Descriptors.InitOnlyInCopy,
            "MW0007" => Descriptors.CircularProjection,
            "MW0008" => Descriptors.MissingElementMap,
            "MW0009" => Descriptors.BadAfterMap,
            _ => throw new InvalidOperationException($"Unknown diagnostic id '{Id}'."),
        };

        var location = Location.Create(
            FilePath,
            new TextSpan(SpanStart, SpanLength),
            new LinePositionSpan(
                new LinePosition(StartLine, StartCharacter),
                new LinePosition(EndLine, EndCharacter)));

        // Arguments are pre-rendered strings; the descriptor's format string consumes them.
        return Diagnostic.Create(descriptor, location, [.. Arguments]);
    }
}
