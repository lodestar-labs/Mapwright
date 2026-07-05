namespace Mapwright;

/// <summary>
/// Declares source properties this mapping deliberately does not read, suppressing the
/// MW0002 unused-source-property diagnostic for them. Useful to document one-way fields
/// (e.g. database-generated audit columns that must not flow back into the domain).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MapIgnoreSourceAttribute(params string[] sourceProperties) : Attribute
{
    public string[] SourceProperties { get; } = sourceProperties;
}
