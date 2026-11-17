namespace Scripts.Syntax;

/// <summary>
/// Name table local to one program: every identifier encountered is reduced to a single canonical
/// instance. Scope resolution (<c>Scope.TryGetLocal</c>, ordinal comparison) then takes its
/// fast path by reference instead of comparing characters.
/// Names are purely ASCII (the lexer rejects non-ASCII); interning therefore changes no
/// comparison result, only the identity of the instances. The table lives for the duration of loading
/// the program and feeds no process-level immortal structure.
/// </summary>
internal sealed class NameTable
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);

    /// <summary>Canonical instance of <paramref name="name"/>; returns the original instance on first encounter.</summary>
    public string Intern(string name)
    {
        if (names.TryGetValue(name, out var canonical))
        {
            return canonical;
        }

        names[name] = name;
        return name;
    }
}
