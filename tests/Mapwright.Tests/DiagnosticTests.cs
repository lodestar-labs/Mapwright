using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Mapwright.Generator;

namespace Mapwright.Tests;

/// <summary>
/// Drives the generator over in-memory compilations to prove the verification story:
/// what AutoMapper deferred to a runtime AssertConfigurationIsValid() call, Mapwright
/// reports while the code is being compiled.
/// </summary>
[TestFixture]
public class DiagnosticTests
{
    private const string Prelude = """
        using System;
        using System.Collections.Generic;
        using Mapwright;

        public class Source
        {
            public int Id { get; set; }
            public string? Name { get; set; }
        }

        public class Target
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Extra { get; set; }
        }

        public record FrozenTarget
        {
            public int Id { get; init; }
        }
        """;

    private static ImmutableArrayWrapper RunGenerator(string mapperSource)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .Append(MetadataReference.CreateFromFile(typeof(MapperAttribute).Assembly.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "MapwrightDiagnosticTests",
            [CSharpSyntaxTree.ParseText(Prelude + Environment.NewLine + mapperSource)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new MapwrightGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();
        return new ImmutableArrayWrapper([.. result.Diagnostics]);
    }

    private sealed record ImmutableArrayWrapper(Diagnostic[] Diagnostics);

    [Test]
    public void Unmapped_destination_property_is_a_build_diagnostic()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                public static partial Target Map(Source source);
            }
            """);

        var diagnostic = run.Diagnostics.Single(d => d.Id == "MW0001");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Warning));
            Assert.That(diagnostic.GetMessage(), Does.Contain("'Extra'"));
        });
    }

    [Test]
    public void Ignoring_the_property_silences_the_diagnostic()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                [MapIgnore(nameof(Target.Extra))]
                public static partial Target Map(Source source);
            }
            """);

        Assert.That(run.Diagnostics.Where(d => d.Id == "MW0001"), Is.Empty);
    }

    [Test]
    public void Stale_ignore_lists_are_errors_not_rot()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                [MapIgnore(nameof(Target.Extra), "RemovedLastSprint")]
                public static partial Target Map(Source source);
            }
            """);

        var diagnostic = run.Diagnostics.Single(d => d.Id == "MW0003");
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Severity, Is.EqualTo(DiagnosticSeverity.Error));
            Assert.That(diagnostic.GetMessage(), Does.Contain("RemovedLastSprint"));
        });
    }

    [Test]
    public void Init_only_destination_cannot_be_copied_in_place()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                public static partial void Copy(Source source, FrozenTarget target);
            }
            """);

        Assert.That(run.Diagnostics.Count(d => d.Id == "MW0006"), Is.EqualTo(1));
    }

    [Test]
    public void Collection_map_without_an_element_map_is_an_error()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                public static partial List<Target> MapAll(IEnumerable<Source> source);
            }
            """);

        Assert.That(run.Diagnostics.Single(d => d.Id == "MW0008").Severity, Is.EqualTo(DiagnosticSeverity.Error));
    }

    [Test]
    public void Missing_after_map_method_is_an_error()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                [MapIgnore(nameof(Target.Extra))]
                [AfterMap("DoesNotExist")]
                public static partial Target Map(Source source);
            }
            """);

        Assert.That(run.Diagnostics.Single(d => d.Id == "MW0009").Severity, Is.EqualTo(DiagnosticSeverity.Error));
    }

    [Test]
    public void Unreadable_source_properties_surface_as_info()
    {
        var run = RunGenerator("""
            [Mapper]
            public static partial class M
            {
                [MapIgnore(nameof(Target.Extra))]
                public static partial Target Map(Source source);
            }
            """);

        // Source.Id and Source.Name are both consumed; nothing to report.
        Assert.That(run.Diagnostics.Where(d => d.Id == "MW0002"), Is.Empty);
    }
}
