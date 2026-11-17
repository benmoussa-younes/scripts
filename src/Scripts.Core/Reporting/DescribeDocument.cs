using Scripts.Core.Json;

namespace Scripts.Core.Reporting;

/// <summary>
/// <c>scripts describe --format json</c> (DESIGN 10): one compact single-line JSON document followed by a newline, generated from the
/// shared catalogs in catalog order, so two runs are byte-identical. Top-level key order: language, version, schema_version,
/// query (only with a name filter), keywords, reserved_identifiers, future_reserved, operators, precedence, types,
/// functions, error_codes, exit_codes, limits, grammar_ebnf. Every type (primitives, generics, records, `error`) lives in the single `types` array,
/// each tagged with a `category`.
/// </summary>
public static class DescribeDocument
{
    /// <summary>Renders the document; <paramref name="name"/> keeps only the matching entries (a qualified name, a namespace, a module or a receiver type).</summary>
    public static string Render(string? name = null)
    {
        var writer = new JsonWriter();
        writer.BeginObject();
        writer.WriteProperty("language", LanguageReference.LanguageName);
        writer.WriteProperty("version", LanguageReference.Version);
        writer.WriteProperty("schema_version", LanguageReference.SchemaVersion);
        if (name is not null)
        {
            // Echo the request so a machine client can correlate an empty result with the filter (P9).
            writer.WriteProperty("query", name);
        }

        writer.WriteStringArrayProperty("keywords", LanguageTables.Keywords);
        writer.WriteStringArrayProperty("reserved_identifiers", LanguageTables.ReservedIdentifiers);
        writer.WriteStringArrayProperty("future_reserved", LanguageTables.FutureReserved);
        WriteOperators(writer);
        WritePrecedence(writer);
        WriteTypes(writer);
        WriteFunctions(writer, name);
        WriteGroups(writer);
        WriteErrorCodes(writer);
        WriteExitCodes(writer);
        WriteLimits(writer);
        writer.WriteProperty("grammar_ebnf", GrammarData.Ebnf);
        writer.EndObject();
        return writer.ToString() + "\n";
    }

    private static void WriteOperators(JsonWriter writer)
    {
        writer.WriteName("operators");
        writer.BeginArray();
        foreach (var operatorInfo in LanguageReference.Operators)
        {
            writer.BeginObject();
            writer.WriteProperty("symbol", operatorInfo.Symbol);
            writer.WriteProperty("category", operatorInfo.Category);
            writer.WriteProperty("description", operatorInfo.Description);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WritePrecedence(JsonWriter writer)
    {
        writer.WriteName("precedence");
        writer.BeginArray();
        foreach (var level in LanguageReference.Precedence)
        {
            writer.BeginObject();
            writer.WriteProperty("level", level.Level);
            writer.WriteStringArrayProperty("operators", level.Symbols);
            writer.WriteProperty("associativity", level.Associativity);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteTypes(JsonWriter writer)
    {
        writer.WriteName("types");
        writer.BeginArray();
        foreach (var type in LanguageReference.Types)
        {
            writer.BeginObject();
            writer.WriteProperty("name", type.Name);
            writer.WriteProperty("syntax", type.Syntax);
            writer.WriteProperty("category", type.Category);
            writer.WriteProperty("description", type.Description);
            writer.EndObject();
        }

        foreach (var typeName in BuiltinTypes.RecordAndErrorTypeNames)
        {
            WriteStructuredType(writer, typeName);
        }

        foreach (var typeName in BuiltinTypes.OpaqueTypeNames)
        {
            WriteStructuredType(writer, typeName);
        }

        writer.EndArray();
    }

    // "kinds" lists the closed value set of the kind field (empty when the type has none).
    private static void WriteStructuredType(JsonWriter writer, string typeName)
    {
        writer.BeginObject();
        writer.WriteProperty("name", typeName);
        writer.WriteProperty("syntax", typeName);
        writer.WriteProperty("category", CategoryOf(typeName));
        writer.WriteProperty("description", BuiltinTypes.SummaryOf(typeName));
        writer.WriteName("fields");
        writer.BeginArray();
        foreach (var (fieldName, fieldType) in BuiltinTypes.FieldsOf(typeName))
        {
            writer.BeginObject();
            writer.WriteProperty("name", fieldName);
            writer.WriteProperty("type", fieldType.ToString());
            writer.EndObject();
        }

        writer.EndArray();
        var kinds = BuiltinTypes.PathKindKindsOf(typeName) ?? BuiltinTypes.KindsOf(typeName) ?? [];
        writer.WriteStringArrayProperty("kinds", kinds);
        writer.EndObject();
    }

    private static void WriteFunctions(JsonWriter writer, string? name)
    {
        writer.WriteName("functions");
        writer.BeginArray();
        foreach (var entry in StdlibCatalog.All.Where(entry => Matches(entry, name)))
        {
            writer.BeginObject();
            writer.WriteProperty("qualified_name", entry.QualifiedName);
            writer.WriteProperty("signature", entry.Signature);
            writer.WriteName("examples");
            writer.BeginArray();
            writer.BeginObject();
            writer.WriteProperty("code", entry.ExampleCode);
            writer.WriteProperty("output", entry.ExampleOutput);
            writer.EndObject();
            writer.EndArray();
            writer.WriteProperty("kind", KindName(entry.Kind));
            writer.WriteProperty("receiver", entry.ReceiverTypeText);
            writer.WriteName("parameters");
            writer.BeginArray();
            foreach (var parameter in entry.Parameters)
            {
                writer.BeginObject();
                writer.WriteProperty("name", parameter.Name);
                writer.WriteProperty("type", parameter.TypeText);
                writer.EndObject();
            }

            writer.EndArray();
            writer.WriteProperty("returns", entry.ReturnTypeText);
            writer.WriteStringArrayProperty("error_types", CollectErrorTypeNames(entry.ReturnTypeText));
            writer.WriteStringArrayProperty("error_kinds", entry.ErrorKinds);
            writer.WriteProperty("dangerous", entry.Dangerous);
            writer.WriteProperty("destructive", entry.Destructive);
            writer.WriteProperty("mutates_receiver", entry.MutatesReceiver);
            writer.WriteProperty("deterministic", entry.Deterministic);
            writer.WriteStringArrayProperty("effects", entry.Effects);
            writer.WriteProperty("description", entry.Summary);
            writer.WriteProperty("semantics", entry.Semantics);
            writer.WriteProperty("explain", entry.ExplainTemplate);
            writer.EndObject();
        }

        writer.EndArray();
    }

    /// <summary>`--name`: an exact qualified name, a namespace prefix (`process`), a module or a receiver type (`list`, `list<T>`).</summary>
    private static bool Matches(StdlibEntry entry, string? name)
    {
        if (name is null)
        {
            return true;
        }

        if (string.Equals(entry.QualifiedName, name, StringComparison.Ordinal)
            || entry.QualifiedName.StartsWith(name + ".", StringComparison.Ordinal)
            || string.Equals(entry.ModuleName, name, StringComparison.Ordinal))
        {
            return true;
        }

        if (entry.ReceiverTypeText is { } receiver
            && (string.Equals(receiver, name, StringComparison.Ordinal)
                || string.Equals(receiver.Split('<')[0], name, StringComparison.Ordinal)))
        {
            return true;
        }

        // `list.append` is the method `list<T>.append` without its type parameters.
        var dot = name.LastIndexOf('.');
        return dot > 0
            && string.Equals(entry.Name, name[(dot + 1)..], StringComparison.Ordinal)
            && entry.ReceiverTypeText is { } methodReceiver
            && string.Equals(methodReceiver.Split('<')[0], name[..dot], StringComparison.Ordinal);
    }

    /// <summary>The rules common to a module or a receiver type: they belong to no single entry.</summary>
    private static void WriteGroups(JsonWriter writer)
    {
        writer.WriteName("groups");
        writer.BeginArray();
        foreach (var group in StdlibNotes.All)
        {
            writer.BeginObject();
            writer.WriteProperty("name", group.Name);
            writer.WriteProperty("kind", group.Kind);
            writer.WriteProperty("notes", group.Notes);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteErrorCodes(JsonWriter writer)
    {
        writer.WriteName("error_codes");
        writer.BeginArray();
        foreach (var error in ErrorCatalog.All)
        {
            writer.BeginObject();
            writer.WriteProperty("code", error.Code);
            writer.WriteProperty("category", error.Category);
            writer.WriteProperty("message", error.Message);
            writer.WriteProperty("cause", error.Cause);
            writer.WriteProperty("suggestion", error.Suggestion);
            writer.WriteProperty("position", error.Position);
            // Optional key (stability policy): `reserved` codes are catalogued for numbering and never emitted,
            // so an agent must not generate their syntax.
            if (error.Status != "active")
            {
                writer.WriteProperty("status", error.Status);
            }

            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteExitCodes(JsonWriter writer)
    {
        writer.WriteName("exit_codes");
        writer.BeginArray();
        foreach (var exitCode in LanguageReference.ExitCodeTable)
        {
            writer.BeginObject();
            writer.WriteProperty("code", exitCode.Code);
            writer.WriteProperty("last_code", exitCode.LastCode);
            writer.WriteProperty("name", exitCode.Name);
            writer.WriteProperty("meaning", exitCode.Meaning);
            writer.EndObject();
        }

        writer.EndArray();
    }

    private static void WriteLimits(JsonWriter writer)
    {
        writer.WriteName("limits");
        writer.BeginObject();
        foreach (var limit in LanguageReference.LimitTable)
        {
            writer.WriteProperty(limit.Name, limit.Value);
        }

        writer.EndObject();
    }

    private static string KindName(StdlibKind kind) => kind switch
    {
        StdlibKind.Function => "function",
        StdlibKind.Method => "method",
        StdlibKind.ModuleFunction => "module_function",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // The category of a structured type entry: the error type, an opaque type (methods but no
    // exposed field, DESIGN §10), or a record (DESIGN §3.4).
    private static string CategoryOf(string typeName) => typeName switch
    {
        _ when ScriptTypes.IsErrorTypeName(typeName) => "error",
        _ when ScriptTypes.IsOpaqueTypeName(typeName) => "opaque",
        _ => "record",
    };

    // Concrete error types named anywhere in the return type, in order of first appearance.
    private static List<string> CollectErrorTypeNames(string returnTypeText)
    {
        var names = new List<string>();
        CollectErrorTypeNames(TypeText.Parse(returnTypeText), names);
        return names;
    }

    private static void CollectErrorTypeNames(ScriptType type, List<string> names)
    {
        switch (type)
        {
            case NamedType named when ScriptTypes.IsErrorTypeName(named.Name) && !names.Contains(named.Name):
                names.Add(named.Name);
                break;
            case ListType list:
                CollectErrorTypeNames(list.Element, names);
                break;
            case MapType map:
                CollectErrorTypeNames(map.Key, names);
                CollectErrorTypeNames(map.Value, names);
                break;
            case ResultType result:
                CollectErrorTypeNames(result.Success, names);
                CollectErrorTypeNames(result.Failure, names);
                break;
            case FunctionType function:
                foreach (var parameter in function.Parameters)
                {
                    CollectErrorTypeNames(parameter, names);
                }

                CollectErrorTypeNames(function.Return, names);
                break;
        }
    }
}
