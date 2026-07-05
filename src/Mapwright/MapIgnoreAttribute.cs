namespace Mapwright;

/// <summary>
/// Declares destination properties this mapping intentionally leaves unmapped — audit
/// fields, navigation properties, values set elsewhere. The generator skips them without
/// raising MW0001, and raises MW0003 if a named property does not exist on the destination
/// (so ignore lists cannot rot as models evolve — the typo is a build error).
/// Mapwright's equivalent of AutoMapper's <c>ForMember(d =&gt; d.P, o =&gt; o.Ignore())</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MapIgnoreAttribute(params string[] targetProperties) : Attribute
{
    public string[] TargetProperties { get; } = targetProperties;
}
