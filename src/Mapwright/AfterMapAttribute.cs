namespace Mapwright;

/// <summary>
/// Names a <c>static void</c> method in the same mapper class — signature
/// <c>(TSource source, TDest result)</c> — that the generated map calls after the
/// property assignments. The escape hatch for the small hand-written remainder of a
/// mapping: computed values, conditional fix-ups, properties listed in
/// <see cref="MapIgnoreAttribute"/> that need bespoke logic. Because it is ordinary code
/// in your own class, it is debuggable and testable like everything else.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AfterMapAttribute(string methodName) : Attribute
{
    public string MethodName { get; } = methodName;
}
