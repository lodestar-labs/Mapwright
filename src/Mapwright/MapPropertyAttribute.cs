namespace Mapwright;

/// <summary>
/// Maps a source property to a differently named destination property. Only needed when
/// the names differ beyond casing — Mapwright matches names case-insensitively by default,
/// so <c>CodeID → CodeId</c> needs no configuration.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class MapPropertyAttribute(string source, string target) : Attribute
{
    public string Source { get; } = source;

    public string Target { get; } = target;
}
