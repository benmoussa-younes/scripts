using System.Text.Json;
using Scripts.Core;
using Scripts.Core.Reporting;

namespace Scripts.Cli.Tests.Describe;

public sealed class DescribeDocumentTests
{
    private static readonly string[] ExpectedTopLevelKeys =
    [
        "language", "version", "schema_version", "keywords", "reserved_identifiers", "future_reserved", "operators",
        "precedence", "types", "functions", "groups", "error_codes", "exit_codes", "limits", "grammar_ebnf",
    ];

    private static readonly string RenderedDocument = DescribeDocument.Render();

    private static JsonDocument Parse() => JsonDocument.Parse(RenderedDocument);

    private static List<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!).ToList();

    [Fact]
    public void A_name_query_is_echoed_after_the_schema_version()
    {
        using var document = JsonDocument.Parse(DescribeDocument.Render("process"));

        var keys = document.RootElement.EnumerateObject().Select(property => property.Name).ToList();
        Assert.Equal(
            ["language", "version", "schema_version", "query", "keywords", "reserved_identifiers", "future_reserved", "operators",
             "precedence", "types", "functions", "groups", "error_codes", "exit_codes", "limits", "grammar_ebnf"],
            keys);
        Assert.Equal("process", document.RootElement.GetProperty("query").GetString());
        Assert.All(
            document.RootElement.GetProperty("functions").EnumerateArray(),
            function => Assert.StartsWith("process.", function.GetProperty("qualified_name").GetString()!, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unknown_name_is_echoed_with_an_empty_catalogue()
    {
        using var document = JsonDocument.Parse(DescribeDocument.Render("not_a_name"));

        Assert.Equal("not_a_name", document.RootElement.GetProperty("query").GetString());
        Assert.Empty(document.RootElement.GetProperty("functions").EnumerateArray());
    }

    [Fact]
    public void The_unfiltered_document_has_no_query_key()
    {
        using var document = Parse();

        Assert.False(document.RootElement.TryGetProperty("query", out _));
    }

    [Fact]
    public void Document_is_valid_json_with_the_documented_top_level_keys_in_order()
    {
        using var document = Parse();

        Assert.Equal(ExpectedTopLevelKeys, document.RootElement.EnumerateObject().Select(property => property.Name).ToList());
        Assert.Equal("scripts", document.RootElement.GetProperty("language").GetString());
        Assert.Equal(LanguageReference.Version, document.RootElement.GetProperty("version").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
    }

    [Fact]
    public void Document_is_one_line_with_a_single_trailing_newline()
    {
        Assert.EndsWith("}\n", RenderedDocument, StringComparison.Ordinal);
        Assert.Equal(RenderedDocument.Length - 1, RenderedDocument.IndexOf('\n', StringComparison.Ordinal));
    }

    [Fact]
    public void Name_filter_keeps_one_entry_and_emits_its_signature()
    {
        using var document = JsonDocument.Parse(DescribeDocument.Render("filesystem.read_file"));
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();

        Assert.Single(functions);
        Assert.Equal("filesystem.read_file", functions[0].GetProperty("qualified_name").GetString());
        Assert.Equal("filesystem.read_file(file_path: path) -> result<string, error>", functions[0].GetProperty("signature").GetString());
    }

    [Fact]
    public void Name_filter_accepts_a_namespace_a_module_a_receiver_and_yields_empty_for_an_unknown_name()
    {
        static int Count(string name) =>
            JsonDocument.Parse(DescribeDocument.Render(name)).RootElement.GetProperty("functions").GetArrayLength();

        Assert.Equal(16, Count("process"));
        Assert.Equal(40, Count("filesystem"));
        Assert.Equal(25, Count("list"));
        Assert.Equal(6, Count("io"));
        Assert.Equal(10, Count("json"));
        Assert.Equal(7, Count("system"));
        Assert.Equal(0, Count("no_such_name"));
    }

    [Fact]
    public void Document_is_deterministic()
    {
        Assert.Equal(RenderedDocument, DescribeDocument.Render());
    }

    [Fact]
    public void Document_contains_no_raw_control_characters_and_has_a_reasonable_size()
    {
        foreach (var character in RenderedDocument.AsSpan(0, RenderedDocument.Length - 1))
        {
            Assert.True(character >= 0x20 && character != 0x7F && !(character >= 0x80 && character <= 0x9F), $"U+{(int)character:X4}");
            Assert.NotEqual('\u2028', character);
            Assert.NotEqual('\u2029', character);
        }

        Assert.InRange(RenderedDocument.Length, 50_000, 1_000_000);
    }

    [Fact]
    public void Word_lists_equal_the_language_tables()
    {
        using var document = Parse();

        Assert.Equal(LanguageTables.Keywords, Strings(document.RootElement.GetProperty("keywords")));
        Assert.Equal(LanguageTables.ReservedIdentifiers, Strings(document.RootElement.GetProperty("reserved_identifiers")));
        Assert.Equal(LanguageTables.FutureReserved, Strings(document.RootElement.GetProperty("future_reserved")));
        Assert.Equal(21, LanguageTables.Keywords.Length);
        Assert.Equal(7, LanguageTables.FutureReserved.Length);
    }

    [Fact]
    public void Operators_cover_every_symbol_of_the_language()
    {
        using var document = Parse();
        var symbols = document.RootElement.GetProperty("operators").EnumerateArray()
            .Select(item => item.GetProperty("symbol").GetString()!).ToList();

        string[] expectedSymbols =
        [
            "+", "-", "*", "/", "%", "==", "!=", "<", "<=", ">", ">=", "and", "or", "not", "=", "+=", "-=", "*=", "/=", "%=",
            "->", "=>", "?", ".", ",", ":", "|", "(", ")", "[", "]", "{", "}",
        ];
        Assert.Equal(expectedSymbols, symbols);
        foreach (var item in document.RootElement.GetProperty("operators").EnumerateArray())
        {
            Assert.Equal(["symbol", "category", "description"], item.EnumerateObject().Select(property => property.Name).ToList());
        }
    }

    [Fact]
    public void Precedence_has_nine_levels_from_weakest_to_strongest()
    {
        using var document = Parse();
        var levels = document.RootElement.GetProperty("precedence").EnumerateArray().ToList();

        Assert.Equal(9, levels.Count);
        Assert.Equal(Enumerable.Range(1, 9), levels.Select(level => level.GetProperty("level").GetInt32()));
        Assert.Equal(["=>"], Strings(levels[0].GetProperty("operators")));
        Assert.Equal(["==", "!=", "<", "<=", ">", ">="], Strings(levels[4].GetProperty("operators")));
        Assert.Equal("none", levels[4].GetProperty("associativity").GetString());
        Assert.Equal("right", levels[0].GetProperty("associativity").GetString());
        Assert.Equal("prefix", levels[7].GetProperty("associativity").GetString());
        Assert.Equal(["()", "[]", ".", "?"], Strings(levels[8].GetProperty("operators")));
    }

    [Fact]
    public void Types_list_primitive_and_constructed_types()
    {
        using var document = Parse();
        var names = document.RootElement.GetProperty("types").EnumerateArray().Select(item => item.GetProperty("name").GetString()!).ToList();

        Assert.Equal(["string", "integer", "float", "boolean", "path", "unit", "never", "list", "map", "result", "function", "process_result", "pipeline_result", "path_status", "file_system_status", "system_information", "process_handle", "directory_entry", "walk_report", "walk_skip", "run_request", "error", "json_value", "file_lock"], names);
        var syntaxes = document.RootElement.GetProperty("types").EnumerateArray().Select(item => item.GetProperty("syntax").GetString()!).ToList();
        Assert.Contains("map<K, V>", syntaxes);
        Assert.Contains("function(A, B) -> R", syntaxes);
        var jsonValueDescription = document.RootElement.GetProperty("types").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "json_value").GetProperty("description").GetString()!;
        Assert.Contains("as_list", jsonValueDescription);
        Assert.Contains("as_map", jsonValueDescription);
    }

    [Fact]
    public void Records_and_error_share_one_types_array_with_a_category_and_typed_fields()
    {
        using var document = Parse();
        var types = document.RootElement.GetProperty("types").EnumerateArray().ToList();
        var structured = types.Where(item => item.TryGetProperty("fields", out _)).ToList();
        Assert.Equal(
            ["process_result", "pipeline_result", "path_status", "file_system_status", "system_information", "process_handle", "directory_entry", "walk_report", "walk_skip", "run_request", "error", "json_value", "file_lock"],
            structured.Select(item => item.GetProperty("name").GetString()!));

        foreach (var item in structured)
        {
            var typeName = item.GetProperty("name").GetString()!;
            Assert.Equal(["name", "syntax", "category", "description", "fields", "kinds"], item.EnumerateObject().Select(property => property.Name).ToList());
            Assert.Equal(
                typeName == "error" ? "error" : ScriptTypes.IsOpaqueTypeName(typeName) ? "opaque" : "record",
                item.GetProperty("category").GetString());
            var fields = item.GetProperty("fields").EnumerateArray()
                .Select(field => (field.GetProperty("name").GetString()!, field.GetProperty("type").GetString()!)).ToList();
            Assert.Equal(BuiltinTypes.FieldsOf(typeName).Select(field => (field.Name, field.Type.ToString())), fields);
        }

        var byName = structured.ToDictionary(item => item.GetProperty("name").GetString()!, item => item);
        Assert.Equal(
            ["file", "directory", "symlink", "other"],
            Strings(byName["path_status"].GetProperty("kinds")));
        Assert.Equal(
            ["file", "directory", "symlink", "other"],
            Strings(byName["directory_entry"].GetProperty("kinds")));
        Assert.Equal(
            ["permission_denied", "invalid_encoding", "other"],
            Strings(byName["walk_skip"].GetProperty("kinds")));
        Assert.Empty(byName["walk_report"].GetProperty("kinds").EnumerateArray());
        Assert.Empty(byName["process_result"].GetProperty("kinds").EnumerateArray());
        Assert.Equal(BuiltinTypes.KindsOf("error")!, Strings(byName["error"].GetProperty("kinds")));
    }

    [Fact]
    public void Functions_are_the_catalog_entries_in_catalog_order()
    {
        using var document = Parse();
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();

        Assert.Equal(207, StdlibCatalog.All.Count);
        Assert.Equal(207, functions.Count);
        Assert.Equal(
            StdlibCatalog.All.Select(entry => entry.QualifiedName),
            functions.Select(item => item.GetProperty("qualified_name").GetString()!));
    }

    [Fact]
    public void Function_entries_have_the_documented_shape_and_catalog_values()
    {
        using var document = Parse();
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();

        string[] expectedKeys =
        [
            "qualified_name", "signature", "examples", "kind", "receiver", "parameters", "returns", "error_types", "error_kinds", "dangerous",
            "destructive", "mutates_receiver", "deterministic", "effects", "description", "semantics", "explain",
        ];
        for (var entryIndex = 0; entryIndex < functions.Count; entryIndex++)
        {
            var entry = StdlibCatalog.All[entryIndex];
            var item = functions[entryIndex];
            Assert.Equal(expectedKeys, item.EnumerateObject().Select(property => property.Name).ToList());
            Assert.Equal(
                entry.Kind switch
                {
                    StdlibKind.Function => "function",
                    StdlibKind.Method => "method",
                    _ => "module_function",
                },
                item.GetProperty("kind").GetString());
            Assert.Equal(entry.ReceiverTypeText, item.GetProperty("receiver").GetString());
            Assert.Equal(
                entry.Parameters.Select(parameter => (parameter.Name, parameter.TypeText)),
                item.GetProperty("parameters").EnumerateArray()
                    .Select(parameter => (parameter.GetProperty("name").GetString()!, parameter.GetProperty("type").GetString()!)));
            Assert.Equal(entry.ReturnTypeText, item.GetProperty("returns").GetString());
            Assert.Equal(entry.ErrorKinds, Strings(item.GetProperty("error_kinds")));
            Assert.Equal(entry.Dangerous, item.GetProperty("dangerous").GetBoolean());
            Assert.Equal(entry.Destructive, item.GetProperty("destructive").GetBoolean());
            Assert.Equal(entry.Deterministic, item.GetProperty("deterministic").GetBoolean());
            Assert.Equal(entry.Effects, Strings(item.GetProperty("effects")));
            Assert.Equal(entry.Summary, item.GetProperty("description").GetString());
            Assert.Equal(entry.Semantics, item.GetProperty("semantics").GetString());
        }
    }

    [Fact]
    public void Semantics_is_the_whole_one_line_semantics_text_starting_with_the_description()
    {
        using var document = Parse();
        var functions = document.RootElement.GetProperty("functions").EnumerateArray().ToList();

        Assert.Equal(functions.Count, StdlibCatalog.All.Count);
        for (var entryIndex = 0; entryIndex < functions.Count; entryIndex++)
        {
            var entry = StdlibCatalog.All[entryIndex];
            var item = functions[entryIndex];
            var semantics = item.GetProperty("semantics").GetString()!;
            Assert.False(string.IsNullOrWhiteSpace(semantics), entry.QualifiedName);
            // `description` is the first sentence of `semantics` (DESIGN 10, contract).
            Assert.StartsWith(entry.Summary, semantics, StringComparison.Ordinal);
            // Collapsed to one line: no line break, tab or double space survives.
            Assert.DoesNotContain('\n', semantics);
            Assert.DoesNotContain('\r', semantics);
            Assert.DoesNotContain('\t', semantics);
            Assert.DoesNotContain("  ", semantics, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Known_entries_have_semantics_longer_than_the_description_and_keep_their_backticks()
    {
        using var document = Parse();
        var byName = document.RootElement.GetProperty("functions").EnumerateArray()
            .ToDictionary(item => item.GetProperty("qualified_name").GetString()!, item => item);

        foreach (var name in new[] { "string.format_template", "process.request" })
        {
            var item = byName[name];
            var description = item.GetProperty("description").GetString()!;
            var semantics = item.GetProperty("semantics").GetString()!;
            Assert.True(semantics.Length > description.Length, $"{name}: semantics is not longer than description");
            Assert.Contains("`", semantics, StringComparison.Ordinal);
        }

        // Measured on the binary: the micro-semantics of `format` (`{{`, `}}`, `invalid_format`) is
        // beyond the index sentence.
        var format = byName["string.format_template"].GetProperty("semantics").GetString()!;
        Assert.Contains("`{{`", format, StringComparison.Ordinal);
        Assert.Contains("invalid_format", format, StringComparison.Ordinal);
    }

    [Fact]
    public void Function_kinds_receivers_and_error_types_are_derived_from_the_signatures()
    {
        using var document = Parse();
        // `qualified_name` is unique across the catalogue (DESIGN §10), so the stable key is
        // `(kind, qualified_name)`.
        var byName = document.RootElement.GetProperty("functions").EnumerateArray()
            .ToDictionary(item => item.GetProperty("kind").GetString() + ":" + item.GetProperty("qualified_name").GetString()!);
        // The pair `(kind, qualified_name)` is unique: no collision in the construction above.
        Assert.Equal(207, byName.Count);

        Assert.Equal(JsonValueKind.Null, byName["module_function:filesystem.read_file"].GetProperty("receiver").ValueKind);
        Assert.Equal("module_function", byName["module_function:filesystem.read_file"].GetProperty("kind").GetString());
        Assert.Equal(["error"], Strings(byName["module_function:filesystem.read_file"].GetProperty("error_types")));
        Assert.Equal(["error"], Strings(byName["module_function:process.run"].GetProperty("error_types")));
        // The four exec* globals are gone; process.request + process.run/run_inherit replace them.
        foreach (var removed in new[] { "exec", "exec_with_input", "exec_with_timeout", "exec_inherit" })
        {
            Assert.DoesNotContain("function:" + removed, byName.Keys);
        }
        Assert.Contains("module_function:process.run", byName.Keys);
        Assert.Contains("module_function:process.run_inherit", byName.Keys);
        Assert.Contains("module_function:process.run_shell", byName.Keys);
        // The global directory calls are gone: the names now belong to the filesystem namespace only.
        foreach (var removed in new[] { "function:list_directory", "function:walk_directory" })
        {
            Assert.DoesNotContain(removed, byName.Keys);
        }

        // The former namespace names are gone too, replaced by the merged list/walk entries.
        foreach (var removed in new[]
        {
            "module_function:filesystem.list_directory_entries",
            "module_function:filesystem.walk_directory_entries",
            "module_function:filesystem.walk_directory_report",
        })
        {
            Assert.DoesNotContain(removed, byName.Keys);
        }

        Assert.Contains("module_function:filesystem.list_directory", byName.Keys);
        Assert.Contains("module_function:filesystem.walk_directory", byName.Keys);
        Assert.Empty(byName["module_function:io.write_standard_output_line"].GetProperty("error_types").EnumerateArray());
        // `assert` is a global function returning unit: no error type, no effect.
        Assert.Equal("unit", byName["function:assert"].GetProperty("returns").GetString());
        Assert.Empty(byName["function:assert"].GetProperty("error_types").EnumerateArray());
        Assert.Equal(["none"], Strings(byName["function:assert"].GetProperty("effects")));
        Assert.Equal("module_function", byName["module_function:environment.get_variable"].GetProperty("kind").GetString());
        Assert.Equal(["error"], Strings(byName["module_function:environment.get_variable"].GetProperty("error_types")));
        Assert.Equal("module_function", byName["module_function:filesystem.sha256_file"].GetProperty("kind").GetString());
        // The five `path.*` counterparts of the methods are gone: only the method forms remain.
        foreach (var removed in new[] { "path.base_name", "path.extension", "path.parent", "path.to_string", "path.is_absolute" })
        {
            Assert.DoesNotContain("module_function:" + removed, byName.Keys);
            Assert.Contains("method:" + removed, byName.Keys);
        }

        Assert.Equal("path", byName["method:path.base_name"].GetProperty("receiver").GetString());
        Assert.Empty(byName["method:path.base_name"].GetProperty("parameters").EnumerateArray());
        // `json.encode` replaces the three `to_json` delegations.
        foreach (var removed in new[] { "string.to_json", "list<T>.to_json", "map<K,V>.to_json" })
        {
            Assert.DoesNotContain("method:" + removed, byName.Keys);
        }

        Assert.Equal("module_function", byName["module_function:json.encode"].GetProperty("kind").GetString());
        Assert.Equal(StdlibCatalog.All.Count(entry => entry.Kind == StdlibKind.Method), byName.Values.Count(item => item.GetProperty("kind").GetString() == "method"));
        Assert.True(byName["module_function:process.run_shell"].GetProperty("dangerous").GetBoolean());
        Assert.True(byName["module_function:filesystem.write_file_overwrite"].GetProperty("destructive").GetBoolean());
        Assert.False(byName["module_function:system.get_information"].GetProperty("deterministic").GetBoolean());
        foreach (var item in byName.Values.Where(item => item.GetProperty("kind").GetString() == "method"))
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("receiver").ValueKind);
        }
    }

    [Fact]
    public void Map_error_has_no_concrete_error_type_because_its_failure_type_is_a_variable()
    {
        using var document = Parse();
        var mapError = document.RootElement.GetProperty("functions").EnumerateArray()
            .Single(item => item.GetProperty("qualified_name").GetString()!.EndsWith("transform_error", StringComparison.Ordinal));

        Assert.Empty(mapError.GetProperty("error_types").EnumerateArray());
    }

    [Fact]
    public void Error_codes_are_the_catalog_entries_with_the_same_fields()
    {
        using var document = Parse();
        var errorCodes = document.RootElement.GetProperty("error_codes").EnumerateArray().ToList();

        Assert.Equal(190, ErrorCatalog.All.Count);
        Assert.Equal(190, errorCodes.Count);
        for (var errorIndex = 0; errorIndex < errorCodes.Count; errorIndex++)
        {
            var expected = ErrorCatalog.All[errorIndex];
            var actual = errorCodes[errorIndex];
            var expectedKeys = expected.Status == "active"
                ? new[] { "code", "category", "message", "cause", "suggestion", "position" }
                : ["code", "category", "message", "cause", "suggestion", "position", "status"];
            Assert.Equal(expectedKeys, actual.EnumerateObject().Select(property => property.Name).ToList());
            if (expected.Status != "active")
            {
                Assert.Equal(expected.Status, actual.GetProperty("status").GetString());
            }

            Assert.Equal(expected.Code, actual.GetProperty("code").GetString());
            Assert.Equal(expected.Category, actual.GetProperty("category").GetString());
            Assert.Equal(expected.Message, actual.GetProperty("message").GetString());
            Assert.Equal(expected.Cause, actual.GetProperty("cause").GetString());
            Assert.Equal(expected.Suggestion, actual.GetProperty("suggestion").GetString());
        }
    }

    [Fact]
    public void Exit_codes_list_every_documented_code_in_ascending_order()
    {
        using var document = Parse();
        var exitCodes = document.RootElement.GetProperty("exit_codes").EnumerateArray().ToList();

        Assert.Equal(
            [0, 1, 250, 251, 252, 253, 254, 129, 130, 141, 143],
            exitCodes.Select(item => item.GetProperty("code").GetInt32()));
        Assert.Equal(ExitCodes.MaximumScriptExitCode, exitCodes[1].GetProperty("last_code").GetInt32());
        Assert.Equal(ExitCodes.Usage, exitCodes[2].GetProperty("last_code").GetInt32());
        Assert.Equal(["code", "last_code", "name", "meaning"], exitCodes[0].EnumerateObject().Select(property => property.Name).ToList());
    }

    [Fact]
    public void Limits_come_from_the_core_constants()
    {
        using var document = Parse();
        var limits = document.RootElement.GetProperty("limits");

        Assert.Equal(Limits.MaximumCallDepth, limits.GetProperty("maximum_call_depth").GetInt32());
        Assert.Equal(10_000, limits.GetProperty("maximum_call_depth").GetInt32());
        Assert.Equal(10_000_000, limits.GetProperty("maximum_range_length").GetInt32());
        Assert.Equal(200, limits.GetProperty("maximum_syntax_depth").GetInt32());
        Assert.Equal(4_194_304, limits.GetProperty("maximum_source_bytes").GetInt32());
        Assert.Equal(268_435_456, limits.GetProperty("maximum_string_length_utf16_units").GetInt32());
        Assert.Equal(268_435_456L, limits.GetProperty("maximum_read_file_bytes").GetInt64());
        Assert.Equal(ExitCodes.MaximumScriptExitCode, limits.GetProperty("maximum_script_exit_code").GetInt32());
        Assert.Equal(
            LanguageReference.LimitTable.Select(limit => limit.Name),
            limits.EnumerateObject().Select(property => property.Name));
    }

    /// <summary>
    ///, "Placeholders of a suggestion (DocGen)": the DocGen validator warns when a suggestion names a
    /// placeholder the message does not show. Those 32 names are the documented contextual ones and must not grow
    /// unnoticed (`docgen check` only warns and still exits 0), so the count is pinned here, with the exact rule.
    /// </summary>
    [Fact]
    public void Suggestion_placeholders_absent_from_the_message_are_the_documented_thirty_two()
    {
        var placeholder = new System.Text.RegularExpressions.Regex(@"\{([a-z_][a-z0-9_]*)\}");
        var contextual = new List<string>();
        foreach (var error in ErrorCatalogData.All)
        {
            var messageNames = placeholder.Matches(error.Message).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            contextual.AddRange(placeholder.Matches(error.Suggestion)
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Where(name => !messageNames.Contains(name))
                .Select(name => error.Code + ":" + name)
                // PARSE-0059 shows the literal string `"\{x\}"` in its suggestion: an example, not a placeholder.
                .Where(entry => entry != ErrorCodes.Parse0059 + ":x"));
        }

        Assert.Equal(32, contextual.Count);
        Assert.All(contextual, entry => Assert.Matches("^SCRIPTS-[A-Z]+-\\d{4}:[a-z_]+$", entry));
    }

    [Fact]
    public void Grammar_text_equals_the_generated_grammar()
    {
        using var document = Parse();

        Assert.Equal(GrammarData.Ebnf, document.RootElement.GetProperty("grammar_ebnf").GetString());
    }
}
