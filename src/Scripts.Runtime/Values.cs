using System.Text;
using Scripts.Core;

namespace Scripts.Runtime;

/// <summary>Runtime value. Equality and hashing are structural (containers compare element-wise; maps ignore order).</summary>
public abstract class Value
{
    public abstract override bool Equals(object? other);

    public abstract override int GetHashCode();

    /// <summary>Deep copy of containers (value semantics); immutable values return themselves.</summary>
    public virtual Value DeepCopy() => this;

    /// <summary>Revokes in-place append permission after the value is observed; only <see cref="StringValue"/> has any.</summary>
    internal virtual void RetireBufferRoot()
    {
    }
}

/// <summary>
/// Immutable text with an optional append buffer that makes repeated <c>x += y</c> linear.
///
/// The buffer is a pure optimisation hidden behind the immutable <see cref="Text"/>. It is only ever grown in
/// place when it already exists, and a buffer only ever exists while the value is the private target of a compound
/// assignment: reading the variable that holds it (see <c>ProgramExecution.EvaluateIdentifier</c>) or observing the
/// value through <see cref="Text"/> retires the buffer immediately. A value that may be aliased (bound by a
/// <c>match</c> pattern, stored in a list/map, passed as an argument) therefore never carries a live buffer, and the
/// buffer of a compound assignment is started on the freshly produced concatenation, never on an aliased operand.
/// Value semantics (DESIGN §3.6) are unchanged: no two independent names ever observe the same in-place growth.
/// </summary>
public sealed class StringValue : Value
{
    /// <summary>Content when no buffer is pending; the materialised cache while a buffer exists.</summary>
    private string text;

    /// <summary>Append buffer, or null once this value is immutable again.</summary>
    private StringBuilder? builder;

    /// <summary>True while <see cref="builder"/> holds characters not yet reflected in <see cref="text"/>.</summary>
    private bool pending;

    public StringValue(string text) => this.text = text;

    /// <summary>
    /// The text. Materialises a pending buffer once and retires it: an observed string must never change again,
    /// because callers may cache the returned reference.
    /// </summary>
    public string Text
    {
        get
        {
            if (pending)
            {
                text = builder!.ToString();
                pending = false;
            }

            builder = null;
            return text;
        }
    }

    /// <summary>True when this value still owns a live append buffer (and is therefore safe to grow in place).</summary>
    internal bool HasAppendBuffer => builder is not null;

    public override bool Equals(object? other) => other is StringValue value && string.Equals(Text, value.Text, StringComparison.Ordinal);

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);

    /// <summary>
    /// Always a distinct wrapper (cheap: the text is shared, not copied), so that an append buffer on one copy can
    /// never be observed through another.
    /// </summary>
    public override Value DeepCopy() => new StringValue(Text);

    /// <summary>Drops the append buffer after the value escapes to another name; the text itself is unchanged.</summary>
    internal override void RetireBufferRoot() => _ = Text;

    /// <summary>
    /// Returns <paramref name="value"/> as a fresh, buffered string ready to grow in place. Only the interpreter calls
    /// this, and only on the freshly produced result of a <c>+=</c>; the value must not be aliased.
    /// </summary>
    public static StringValue StartAppendBuffer(StringValue value)
    {
        var buffered = new StringBuilder(value.Text);
        return new StringValue(buffered);
    }

    private StringValue(StringBuilder builder)
    {
        text = string.Empty;
        this.builder = builder;
        pending = true;
    }

    /// <summary>
    /// <c>text + other</c>. When the left operand already owns a live append buffer (it is the private accumulator
    /// of a <c>+=</c> loop), it is grown in place; otherwise the concatenation produces a fresh value.
    /// </summary>
    public static StringValue Concatenate(StringValue left, StringValue right)
    {
        var rightText = right.Text;
        var leftLength = left.pending ? left.builder!.Length : left.text.Length;
        SizeLimits.EnsureStringLength((long)leftLength + rightText.Length);

        if (left.builder is not null)
        {
            left.builder.Append(rightText);
            left.pending = true;
            return left;
        }

        return new StringValue(string.Concat(left.text, rightText));
    }
}

public sealed class PathValue(string text) : Value
{
    public string Text { get; } = text;

    public override bool Equals(object? other) => other is PathValue value && string.Equals(Text, value.Text, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(1, Text.GetHashCode(StringComparison.Ordinal));
}

public sealed class IntegerValue(long number) : Value
{
    /// <summary>Lower bound of the small-integer cache (inclusive).</summary>
    private const int Low = -128;

    /// <summary>Upper bound of the small-integer cache (inclusive).</summary>
    private const int High = 255;

    /// <summary>Shared instances of small integers: an immutable value can be reused safely.</summary>
    private static readonly IntegerValue[] Cache = BuildCache();

    public long Number { get; } = number;

    private static IntegerValue[] BuildCache()
    {
        var entries = new IntegerValue[High - Low + 1];
        for (var index = 0; index < entries.Length; index++)
        {
            entries[index] = new IntegerValue(Low + index);
        }

        return entries;
    }

    /// <summary>Returns the shared instance for a small integer, otherwise a fresh instance. Value and equality unchanged.</summary>
    public static IntegerValue From(long number) =>
        number >= Low && number <= High ? Cache[number - Low] : new IntegerValue(number);

    public override bool Equals(object? other) => other is IntegerValue value && Number == value.Number;

    public override int GetHashCode() => Number.GetHashCode();
}

/// <summary>Always finite; construction of NaN or infinity is a runtime failure raised by the caller before building the value.</summary>
public sealed class FloatValue(double number) : Value
{
    public double Number { get; } = number;

    public override bool Equals(object? other) => other is FloatValue value && Number == value.Number;

    public override int GetHashCode() => Number.GetHashCode();
}

public sealed class BooleanValue : Value
{
    public static readonly BooleanValue True = new(true);
    public static readonly BooleanValue False = new(false);

    private BooleanValue(bool flag) => Flag = flag;

    public bool Flag { get; }

    public static BooleanValue From(bool flag) => flag ? True : False;

    public override bool Equals(object? other) => other is BooleanValue value && Flag == value.Flag;

    public override int GetHashCode() => Flag.GetHashCode();
}

public sealed class UnitValue : Value
{
    public static readonly UnitValue Instance = new();

    private UnitValue()
    {
    }

    public override bool Equals(object? other) => other is UnitValue;

    public override int GetHashCode() => 0;
}

public sealed class ListValue(List<Value> items) : Value
{
    public ListValue()
        : this([])
    {
    }

    public List<Value> Items { get; } = items;

    public override Value DeepCopy()
    {
        var copies = new List<Value>(Items.Count);
        foreach (var item in Items)
        {
            copies.Add(item.DeepCopy());
        }

        return new ListValue(copies);
    }

    public override bool Equals(object? other)
    {
        if (other is not ListValue value || value.Items.Count != Items.Count)
        {
            return false;
        }

        for (var index = 0; index < Items.Count; index++)
        {
            if (!Items[index].Equals(value.Items[index]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }
}

/// <summary>Insertion-ordered map. Keys are StringValue, IntegerValue, PathValue or BooleanValue.</summary>
public sealed class MapValue : Value
{
    private readonly List<Value> keys = [];
    private readonly List<Value> values = [];
    private readonly Dictionary<Value, int> positions = [];

    public int Count => keys.Count;

    public IReadOnlyList<Value> Keys => keys;

    public IReadOnlyList<Value> Values => values;

    public bool TryGet(Value key, out Value value)
    {
        if (positions.TryGetValue(key, out var position))
        {
            value = values[position];
            return true;
        }

        value = UnitValue.Instance;
        return false;
    }

    public bool ContainsKey(Value key) => positions.ContainsKey(key);

    /// <summary>Appends a new key at the end, or replaces the value of an existing key keeping its position.</summary>
    public void Set(Value key, Value value)
    {
        if (positions.TryGetValue(key, out var position))
        {
            values[position] = value;
            return;
        }

        positions[key] = keys.Count;
        keys.Add(key);
        values.Add(value);
    }

    public bool Remove(Value key)
    {
        if (!positions.TryGetValue(key, out var position))
        {
            return false;
        }

        keys.RemoveAt(position);
        values.RemoveAt(position);
        positions.Clear();
        for (var index = 0; index < keys.Count; index++)
        {
            positions[keys[index]] = index;
        }

        return true;
    }

    public override Value DeepCopy()
    {
        var copy = new MapValue();
        for (var index = 0; index < keys.Count; index++)
        {
            // Keys are copied too: a string key may carry an append buffer, and two independent maps must never
            // observe the same in-place growth (value semantics, DESIGN 3.6).
            copy.Set(keys[index].DeepCopy(), values[index].DeepCopy());
        }

        return copy;
    }

    public override bool Equals(object? other)
    {
        if (other is not MapValue value || value.Count != Count)
        {
            return false;
        }

        for (var index = 0; index < keys.Count; index++)
        {
            if (!value.TryGet(keys[index], out var otherValue) || !values[index].Equals(otherValue))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = 0;
        for (var index = 0; index < keys.Count; index++)
        {
            hash ^= HashCode.Combine(keys[index], values[index]);
        }

        return hash;
    }
}

/// <summary>ok(payload) or error(payload); ok() carries UnitValue.Instance.</summary>
public sealed class ResultValue : Value
{
    private ResultValue(bool isOk, Value payload)
    {
        IsOk = isOk;
        Payload = payload;
    }

    public bool IsOk { get; }

    public Value Payload { get; }

    public static ResultValue Ok(Value payload) => new(true, payload);

    public static ResultValue Error(Value payload) => new(false, payload);

    public override Value DeepCopy() => new ResultValue(IsOk, Payload.DeepCopy());

    public override bool Equals(object? other) => other is ResultValue value && IsOk == value.IsOk && Payload.Equals(value.Payload);

    public override int GetHashCode() => HashCode.Combine(IsOk, Payload);
}

/// <summary>Built-in record or error value (process_result, path_status, error...). Fields follow BuiltinTypes.FieldsOf order.</summary>
public sealed class RecordValue : Value
{
    private readonly Value[] fieldValues;

    public RecordValue(string typeName, params Value[] fieldValues)
    {
        var fields = BuiltinTypes.FieldsOf(typeName);
        if (fields.Count != fieldValues.Length)
        {
            throw new ArgumentException($"{typeName} expects {fields.Count} fields, {fieldValues.Length} given", nameof(fieldValues));
        }

        TypeName = typeName;
        this.fieldValues = fieldValues;
    }

    public string TypeName { get; }

    public Value Get(string fieldName)
    {
        var fields = BuiltinTypes.FieldsOf(TypeName);
        for (var index = 0; index < fields.Count; index++)
        {
            if (fields[index].Name == fieldName)
            {
                return fieldValues[index];
            }
        }

        throw new ArgumentException($"{TypeName} has no field {fieldName}", nameof(fieldName));
    }

    public string GetText(string fieldName) => ((StringValue)Get(fieldName)).Text;

    public long GetInteger(string fieldName) => ((IntegerValue)Get(fieldName)).Number;

    public override Value DeepCopy()
    {
        var copies = new Value[fieldValues.Length];
        for (var index = 0; index < fieldValues.Length; index++)
        {
            copies[index] = fieldValues[index].DeepCopy();
        }

        return new RecordValue(TypeName, copies);
    }

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
/// Opaque decoded JSON value (DESIGN §7.10b). scripts containers are homogeneous and
/// JSON is not, so a decoded document is carried as this reference type and read
/// through its accessor methods (<c>kind</c>, <c>as_string</c>, …). It wraps a
/// <c>global::Core.Serialization</c> <c>DataNode</c> tree; identity semantics,
/// like the other opaque handle.
/// </summary>
public sealed class JsonValue(global::Core.Serialization.Values.DataNode node) : Value
{
    public global::Core.Serialization.Values.DataNode Node { get; } = node;

    public override Value DeepCopy() => this;

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>
/// Opaque advisory exclusive lock handle (DESIGN §3.1). Identity semantics: copies share the same
/// descriptor, and <c>file_lock.release()</c> closes it once. Releasing is idempotent; the lock is
/// also released when the process exits.
/// </summary>
public sealed class FileLockValue(int descriptor, string lockPath) : Value
{
    private int fileDescriptor = descriptor;

    /// <summary>The lock path as passed by the script, used as the subject of a failure.</summary>
    public string LockPath { get; } = lockPath;

    public int FileDescriptor => Volatile.Read(ref fileDescriptor);

    public bool IsReleased => FileDescriptor < 0;

    /// <summary>Returns the descriptor and marks the handle released; -1 when it was already released.</summary>
    public int TakeFileDescriptor() => Interlocked.Exchange(ref fileDescriptor, -1);

    public override Value DeepCopy() => this;

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>User function or lambda closure; the interpreter derives from this. Stdlib code calls it through RuntimeContext.Invoke.</summary>
public abstract class FunctionValue : Value
{
    public abstract int Arity { get; }

    public override bool Equals(object? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}
