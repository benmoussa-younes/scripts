using System.Diagnostics.CodeAnalysis;

namespace Scripts.Runtime.Interpreter;

/// <summary>Lexical scope: a few bindings are kept in arrays, a dictionary takes over once the scope grows.</summary>
internal sealed class Scope(Scope? parent, FileEnvironment fileEnvironment)
{
    private const int LinearSearchCapacity = 8;

    private string[]? names;
    private Binding[]? bindings;
    private int count;
    private Dictionary<string, Binding>? index;

    public Scope? Parent { get; } = parent;

    public FileEnvironment FileEnvironment { get; } = fileEnvironment;

    public void Declare(string name, Binding binding)
    {
        if (index is not null)
        {
            index[name] = binding;
            return;
        }

        if (count == LinearSearchCapacity)
        {
            PromoteToDictionary();
            index![name] = binding;
            return;
        }

        names ??= new string[4];
        bindings ??= new Binding[4];
        if (count == names.Length)
        {
            Array.Resize(ref names, count * 2);
            Array.Resize(ref bindings, count * 2);
        }

        names[count] = name;
        bindings[count] = binding;
        count++;
    }

    public bool TryGetLocal(string name, [MaybeNullWhen(false)] out Binding binding)
    {
        if (index is not null)
        {
            return index.TryGetValue(name, out binding);
        }

        var localNames = names;
        for (var position = count - 1; position >= 0; position--)
        {
            if (string.Equals(localNames![position], name, StringComparison.Ordinal))
            {
                binding = bindings![position];
                return true;
            }
        }

        binding = null;
        return false;
    }

    public Binding? Find(string name)
    {
        for (var scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope.TryGetLocal(name, out var binding))
            {
                return binding;
            }
        }

        return null;
    }

    private void PromoteToDictionary()
    {
        index = new Dictionary<string, Binding>(count * 2, StringComparer.Ordinal);
        for (var position = 0; position < count; position++)
        {
            index[names![position]] = bindings![position];
        }

        names = null;
        bindings = null;
    }
}
