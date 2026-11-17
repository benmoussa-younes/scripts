using System.Text;

namespace Scripts.Core;

/// <summary>Semantic types. Structural equality; ToString renders canonical scripts type syntax.</summary>
public abstract record ScriptType
{
    public sealed override string ToString()
    {
        var builder = new StringBuilder();
        Render(builder);
        return builder.ToString();
    }

    internal abstract void Render(StringBuilder builder);
}

public sealed record PrimitiveType(string Name) : ScriptType
{
    internal override void Render(StringBuilder builder) => builder.Append(Name);
}

public sealed record ListType(ScriptType Element) : ScriptType
{
    internal override void Render(StringBuilder builder)
    {
        builder.Append("list<");
        Element.Render(builder);
        builder.Append('>');
    }
}

public sealed record MapType(ScriptType Key, ScriptType Value) : ScriptType
{
    internal override void Render(StringBuilder builder)
    {
        builder.Append("map<");
        Key.Render(builder);
        builder.Append(", ");
        Value.Render(builder);
        builder.Append('>');
    }
}

public sealed record ResultType(ScriptType Success, ScriptType Failure) : ScriptType
{
    internal override void Render(StringBuilder builder)
    {
        builder.Append("result<");
        Success.Render(builder);
        builder.Append(", ");
        Failure.Render(builder);
        builder.Append('>');
    }
}

public sealed record FunctionType(IReadOnlyList<ScriptType> Parameters, ScriptType Return) : ScriptType
{
    public bool Equals(FunctionType? other) =>
        other is not null && Return.Equals(other.Return) && Parameters.SequenceEqual(other.Parameters);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Return);
        foreach (var parameter in Parameters)
        {
            hash.Add(parameter);
        }

        return hash.ToHashCode();
    }

    internal override void Render(StringBuilder builder)
    {
        builder.Append("function(");
        for (var index = 0; index < Parameters.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            Parameters[index].Render(builder);
        }

        builder.Append(") -> ");
        Return.Render(builder);
    }
}

/// <summary>Built-in record or error type such as process_result or error.</summary>
public sealed record NamedType(string Name) : ScriptType
{
    internal override void Render(StringBuilder builder) => builder.Append(Name);
}

/// <summary>Type variable of a stdlib signature (T, U, K, V, E, F); never appears in user programs.</summary>
public sealed record TypeVariable(string Name) : ScriptType
{
    internal override void Render(StringBuilder builder) => builder.Append(Name);
}

/// <summary>Internal poison type used to suppress cascading errors; compatible with everything.</summary>
public sealed record ErrorType : ScriptType
{
    public static readonly ErrorType Instance = new();

    internal override void Render(StringBuilder builder) => builder.Append("<error>");
}

public static class ScriptTypes
{
    public static readonly PrimitiveType String = new("string");
    public static readonly PrimitiveType Integer = new("integer");
    public static readonly PrimitiveType Float = new("float");
    public static readonly PrimitiveType Boolean = new("boolean");
    public static readonly PrimitiveType Path = new("path");
    public static readonly PrimitiveType Unit = new("unit");
    public static readonly PrimitiveType Never = new("never");

    public static bool IsPrimitiveName(string name) =>
        name is "string" or "integer" or "float" or "boolean" or "path" or "unit" or "never";

    public static bool IsRecordOrErrorName(string name) =>
        name is "process_result" or "pipeline_result" or "path_status" or "file_system_status" or "system_information" or "process_handle"
            or "directory_entry" or "walk_report" or "walk_skip" or "run_request" or "error";

    /// <summary>Opaque built-in value type with methods but no public fields (json_value, file_lock).</summary>
    public static bool IsOpaqueTypeName(string name) => name is "json_value" or "file_lock";

    /// <summary>Every nominal (non-primitive, non-generic) built-in type name.</summary>
    public static bool IsNominalTypeName(string name) => IsRecordOrErrorName(name) || IsOpaqueTypeName(name);

    public static bool IsErrorTypeName(string name) =>
        name is "error";

    public static bool IsTypeVariableName(string name) =>
        name.Length == 1 && "TUKVEF".Contains(name[0], StringComparison.Ordinal);
}

/// <summary>Fields of the built-in records and error types (DESIGN 3.4 / 3.5), in declaration order.</summary>
public static class BuiltinTypes
{
    private static readonly Dictionary<string, (string Name, ScriptType Type)[]> FieldTable = new(StringComparer.Ordinal)
    {
        ["process_result"] =
        [
            ("exit_code", ScriptTypes.Integer), ("standard_output", ScriptTypes.String), ("standard_error", ScriptTypes.String),
            ("program", ScriptTypes.String),
        ],
        ["pipeline_result"] =
        [
            ("stage_results", new ListType(new NamedType("process_result"))), ("standard_output", ScriptTypes.String),
        ],
        ["path_status"] =
        [
            ("kind", ScriptTypes.String), ("size_bytes", ScriptTypes.Integer),
            ("modified_unix_seconds", ScriptTypes.Integer), ("permissions", ScriptTypes.Integer),
            ("owner_user_id", ScriptTypes.Integer), ("owner_group_id", ScriptTypes.Integer),
            ("device_major", ScriptTypes.Integer), ("device_minor", ScriptTypes.Integer),
            ("inode", ScriptTypes.Integer), ("link_count", ScriptTypes.Integer),
            ("accessed_unix_seconds", ScriptTypes.Integer), ("changed_unix_seconds", ScriptTypes.Integer),
        ],
        ["file_system_status"] =
        [
            ("block_size", ScriptTypes.Integer), ("fragment_size", ScriptTypes.Integer),
            ("blocks", ScriptTypes.Integer), ("free_blocks", ScriptTypes.Integer),
            ("available_blocks", ScriptTypes.Integer), ("total_inodes", ScriptTypes.Integer),
            ("free_inodes", ScriptTypes.Integer), ("available_inodes", ScriptTypes.Integer),
            ("file_system_identifier", ScriptTypes.Integer), ("maximum_name_length", ScriptTypes.Integer),
        ],
        ["system_information"] =
        [
            ("kernel_name", ScriptTypes.String), ("host_name", ScriptTypes.String),
            ("kernel_release", ScriptTypes.String), ("machine_architecture", ScriptTypes.String),
        ],
        ["process_handle"] = [("process_identifier", ScriptTypes.Integer)],
        ["directory_entry"] = [("path", ScriptTypes.Path), ("kind", ScriptTypes.String)],
        ["walk_report"] =
        [
            ("entries", new ListType(new NamedType("directory_entry"))),
            ("skipped_paths", new ListType(new NamedType("walk_skip"))),
        ],
        ["walk_skip"] = [("path_text", ScriptTypes.String), ("kind", ScriptTypes.String)],
        ["run_request"] =
        [
            ("program", ScriptTypes.String), ("arguments", new ListType(ScriptTypes.String)), ("standard_input", ScriptTypes.String),
            ("working_directory", ScriptTypes.Path), ("environment_overlay", new MapType(ScriptTypes.String, ScriptTypes.String)),
            ("timeout_milliseconds", ScriptTypes.Integer), ("standard_input_file", ScriptTypes.Path),
        ],
        ["error"] = [("kind", ScriptTypes.String), ("message", ScriptTypes.String), ("subject", ScriptTypes.String)],
    };

    private static readonly Dictionary<string, string[]> KindTable = new(StringComparer.Ordinal)
    {
        ["error"] =
        [
            // Union of the former error sets (I/O, process, value), values unchanged;
            // the duplicates not_found / permission_denied / other merge (21 unique values).
            "not_found", "permission_denied", "already_exists", "not_a_directory", "is_a_directory",
            "directory_not_empty", "invalid_path", "invalid_encoding", "too_large", "cross_device", "same_file",
            "invalid_argument", "timed_out",
            "invalid_integer", "invalid_float", "invalid_regex", "invalid_format", "out_of_range",
            "missing_key", "missing_index",
            // json: decoding / encoding failures (same `kind` as the rest, no `JsonError` type).
            "invalid_syntax", "depth_exceeded", "not_encodable",
            // parallel: refused worker bound, transform/visit failure, unspawnable child (no `ParallelError`).
            "invalid_worker_count", "transform_failed", "spawn_failed",
            "other", "end_of_file",
        ],
        ["walk_skip"] = ["permission_denied", "invalid_encoding", "other"],
    };

    public static IReadOnlyList<string> RecordAndErrorTypeNames { get; } =
        ["process_result", "pipeline_result", "path_status", "file_system_status", "system_information", "process_handle", "directory_entry", "walk_report", "walk_skip", "run_request", "error"];

    /// <summary>Opaque built-in value types with methods but no public fields.</summary>
    public static IReadOnlyList<string> OpaqueTypeNames { get; } = ["json_value", "file_lock"];

    public static IReadOnlyList<(string Name, ScriptType Type)> FieldsOf(string typeName) =>
        FieldTable.TryGetValue(typeName, out var fields) ? fields : [];

    public static ScriptType? FieldType(string typeName, string fieldName)
    {
        foreach (var (name, type) in FieldsOf(typeName))
        {
            if (name == fieldName)
            {
                return type;
            }
        }

        return null;
    }

    /// <summary>Closed set of allowed values of the kind field, or null when the type has none.</summary>
    public static IReadOnlyList<string>? KindsOf(string typeName) => KindTable.TryGetValue(typeName, out var kinds) ? kinds : null;

    public static IReadOnlyList<string> PathStatusKinds { get; } = ["file", "directory", "symlink", "other"];

    /// <summary>Closed set of the <c>kind</c> field of a record that classifies a path, or null when it has none.</summary>
    public static IReadOnlyList<string>? PathKindKindsOf(string typeName) =>
        typeName is "path_status" or "directory_entry" ? PathStatusKinds : null;

    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["process_result"] = "result of a program: exit code, captured outputs and the program string as passed",
        ["pipeline_result"] = "pipeline result: one process_result per stage and the last stage's captured stdout",
        ["path_status"] = "state of a path: kind, size, dates, permissions, ownership and inode metadata",
        ["file_system_status"] = "state of the file system containing a path: block and inode counts, sizes, identifier and maximum name length",
        ["system_information"] = "system information: kernel, host and architecture",
        ["process_handle"] = "opaque reference to a launched process",
        ["directory_entry"] = "directory entry: path and kind (file, directory, symlink, other)",
        ["walk_report"] = "partial directory walk: the entries read and the paths skipped",
        ["walk_skip"] = "path skipped by a partial walk: path and reason (permission_denied, invalid_encoding, other)",
        ["run_request"] = "execution request: program, arguments, standard input (a string or a file), working directory, environment overlay and deadline, for process.run, process.run_inherit, parallel.run_all, process.pipeline and process.pipeline_to_file_overwrite",
        ["error"] = "predefined error: kind (closed set), informational message and subject (context)",
        ["json_value"] = "decoded JSON value, opaque: read through its accessors kind/as_string/as_integer/as_float/as_boolean and its container accesses as_list/as_map",
        ["file_lock"] = "advisory exclusive lock handle, opaque: acquired by filesystem.acquire_file_lock and released by file_lock.release",
    };

    public static string SummaryOf(string typeName) => Summaries.TryGetValue(typeName, out var summary) ? summary : string.Empty;
}

/// <summary>Parses canonical type text from stdlib signatures: list&lt;T&gt;, result&lt;unit, error&gt;, function(T) -&gt; boolean.</summary>
public static class TypeText
{
    public static ScriptType Parse(string text)
    {
        var position = 0;
        var type = ParseType(text, ref position);
        SkipSpaces(text, ref position);
        if (position != text.Length)
        {
            throw new FormatException($"Invalid type text: '{text}'");
        }

        return type;
    }

    private static ScriptType ParseType(string text, ref int position)
    {
        SkipSpaces(text, ref position);
        var start = position;
        while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_'))
        {
            position++;
        }

        var name = text[start..position];
        if (name.Length == 0)
        {
            throw new FormatException($"Type expected in '{text}' at {position}");
        }

        SkipSpaces(text, ref position);
        if (name == "function")
        {
            Expect(text, ref position, '(');
            var parameters = new List<ScriptType>();
            SkipSpaces(text, ref position);
            if (position < text.Length && text[position] != ')')
            {
                do
                {
                    parameters.Add(ParseType(text, ref position));
                    SkipSpaces(text, ref position);
                }
                while (TryConsume(text, ref position, ','));
            }

            Expect(text, ref position, ')');
            Expect(text, ref position, '-');
            Expect(text, ref position, '>');
            return new FunctionType(parameters, ParseType(text, ref position));
        }

        if (position < text.Length && text[position] == '<')
        {
            position++;
            var arguments = new List<ScriptType>();
            do
            {
                arguments.Add(ParseType(text, ref position));
                SkipSpaces(text, ref position);
            }
            while (TryConsume(text, ref position, ','));
            Expect(text, ref position, '>');
            return (name, arguments.Count) switch
            {
                ("list", 1) => new ListType(arguments[0]),
                ("map", 2) => new MapType(arguments[0], arguments[1]),
                ("result", 2) => new ResultType(arguments[0], arguments[1]),
                _ => throw new FormatException($"Unknown generic type '{name}' in '{text}'"),
            };
        }

        if (ScriptTypes.IsPrimitiveName(name))
        {
            return new PrimitiveType(name);
        }

        if (ScriptTypes.IsTypeVariableName(name))
        {
            return new TypeVariable(name);
        }

        return new NamedType(name);
    }

    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && text[position] == ' ')
        {
            position++;
        }
    }

    private static bool TryConsume(string text, ref int position, char expected)
    {
        SkipSpaces(text, ref position);
        if (position < text.Length && text[position] == expected)
        {
            position++;
            return true;
        }

        return false;
    }

    private static void Expect(string text, ref int position, char expected)
    {
        if (!TryConsume(text, ref position, expected))
        {
            throw new FormatException($"'{expected}' expected in '{text}' at {position}");
        }
    }
}
