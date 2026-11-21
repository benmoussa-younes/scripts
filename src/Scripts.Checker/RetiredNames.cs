namespace Scripts.Checker;

/// <summary>
/// The earlier-revision renames (the locked nomenclature): every retired spelling and its replacement.
/// SCRIPTS-TYPE-0068 names the pair and carries a machine fix when the replacement is a plain rename.
/// There is no alias and no deprecation window: the old name does not resolve anywhere else.
/// </summary>
internal static class RetiredNames
{
    /// <summary>Global functions that became module functions (a plain qualified rename).</summary>
    private static readonly Dictionary<string, string> Globals = new(StringComparer.Ordinal)
    {
        ["print"] = "io.write_standard_output_line",
        ["eprint"] = "io.write_standard_error_line",
        ["exit_script"] = "process.exit_with_code",
        ["script_arguments"] = "process.arguments",
        ["process_id"] = "process.identifier",
        ["which"] = "process.find_program_path",
        ["uname"] = "system.get_information",
        ["whoami"] = "system.user_name",
        ["current_directory"] = "filesystem.get_current_directory",
        ["change_directory"] = "filesystem.set_current_directory",
        ["read_file"] = "filesystem.read_file",
        ["read_file_bounded"] = "filesystem.read_file_bounded",
        ["write_file"] = "filesystem.write_file_overwrite",
        ["write_file_atomically"] = "filesystem.write_file_overwrite_atomically",
        ["append_file"] = "filesystem.append_file",
        ["path_exists"] = "filesystem.path_exists",
        ["get_file_status"] = "filesystem.get_path_status",
        ["remove_file"] = "filesystem.remove_file",
        ["remove_empty_directory"] = "filesystem.remove_empty_directory",
        ["remove_directory_tree"] = "filesystem.remove_directory_tree",
        ["make_directory"] = "filesystem.make_directory",
        ["make_directory_tree"] = "filesystem.make_directory_tree",
        ["make_temporary_directory"] = "filesystem.make_temporary_directory",
        ["list_directory"] = "filesystem.list_directory",
        ["walk_directory"] = "filesystem.walk_directory",
        ["copy_file"] = "filesystem.copy_file",
        ["copy_file_overwrite"] = "filesystem.copy_file_overwrite",
        ["move_path"] = "filesystem.move_path",
        ["move_path_overwrite"] = "filesystem.move_path_overwrite",
        ["sha256_file"] = "filesystem.sha256_file",
    };

    /// <summary>Global functions that became methods or explicit requests: prose only, no blind rename.</summary>
    private static readonly Dictionary<string, string> Conversions = new(StringComparer.Ordinal)
    {
        ["format"] = "`(template).format_template(values)`",
        ["to_path"] = "`(text).to_path()`",
        ["exec_shell"] = "`process.run(process.request(program, arguments))`",
    };

    /// <summary>Module members that were removed with a structured replacement (prose only, no blind rename).</summary>
    private static readonly Dictionary<(string Module, string Old), string> ModuleMemberConversions = new()
    {
        [("process", "run_shell")] = "`process.run(process.request(program, arguments))`",
    };

    /// <summary>Module members whose name changed inside the same namespace.</summary>
    private static readonly Dictionary<(string Module, string Old), string> ModuleMembers = new()
    {
        [("environment", "get")] = "get_variable",
        [("environment", "set")] = "set_variable",
        [("environment", "remove")] = "remove_variable",
        [("environment", "all")] = "all_variables",
        [("io", "write")] = "write_standard_output",
        [("io", "read_line")] = "read_standard_input_line",
        [("filesystem", "write_file_atomically")] = "write_file_overwrite_atomically",
        [("process", "kill")] = "terminate",
        [("parallel", "map")] = "transform",
        [("parallel", "exec_all")] = "run_all",
        [("parallel", "walk")] = "walk_directory",
    };

    /// <summary>Methods whose name changed on the same receiver type (the replacement keeps the arity).</summary>
    private static readonly Dictionary<(string Receiver, string Old), string> Methods = new()
    {
        [("path", "to_text")] = "to_string",
        [("string", "trim")] = "trim_whitespace",
        [("string", "to_uppercase")] = "uppercased",
        [("string", "to_lowercase")] = "lowercased",
        [("string", "regex_captures")] = "capture_groups",
        [("string", "remove_prefix")] = "strip_prefix",
        [("string", "remove_suffix")] = "strip_suffix",
        [("list", "get")] = "get_at_index",
        [("list", "any")] = "any_match",
        [("list", "all")] = "all_match",
        [("list", "find")] = "find_first",
        [("list", "min")] = "minimum",
        [("list", "max")] = "maximum",
        [("map", "get")] = "get_value",
        [("result", "unwrap")] = "value_or_fail",
        [("result", "unwrap_or")] = "value_or",
        [("result", "map_error")] = "transform_error",
    };

    /// <summary>Built-in type names that changed.</summary>
    private static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["file_status"] = "path_status",
        ["exec_request"] = "run_request",
    };

    /// <summary>Record fields whose name changed, per record type.</summary>
    private static readonly Dictionary<(string Type, string Old), string> Fields = new()
    {
        [("process_result", "stdout")] = "standard_output",
        [("process_result", "stderr")] = "standard_error",
        [("pipeline_result", "results")] = "stage_results",
        [("pipeline_result", "stdout")] = "standard_output",
        [("walk_report", "skipped")] = "skipped_paths",
        [("walk_skip", "path")] = "path_text",
        [("run_request", "environment")] = "environment_overlay",
    };

    /// <summary>Named arguments whose parameter was renamed, per qualified catalogue entry.</summary>
    private static readonly Dictionary<(string Entry, string Old), string> Parameters = new()
    {
        [("parallel.transform", "workers")] = "worker_count",
        [("parallel.transform", "transform")] = "mapper",
        [("parallel.run_all", "workers")] = "worker_count",
        [("parallel.walk_directory", "workers")] = "worker_count",
        [("list<T>.sorted_by", "key")] = "key_function",
        [("string.contains", "fragment")] = "fragment_text",
        [("string.substring", "start_value")] = "start_index",
        [("run_request.with_working_directory", "directory")] = "directory_path",
        [("run_request.with_environment", "overlay")] = "environment_overlay",
        [("run_request.with_timeout", "milliseconds")] = "timeout_milliseconds",
    };

    /// <summary>Global function renamed to a module function: the replacement is safe to substitute verbatim.</summary>
    public static bool TryGlobal(string oldName, out string replacement) => Globals.TryGetValue(oldName, out replacement!);

    /// <summary>Global function that became a method: prose only, no blind rename.</summary>
    public static bool TryConversion(string oldName, out string rewrite) => Conversions.TryGetValue(oldName, out rewrite!);

    public static bool TryModuleMember(string moduleName, string oldMember, out string replacement) =>
        ModuleMembers.TryGetValue((moduleName, oldMember), out replacement!);

    public static bool TryModuleMemberConversion(string moduleName, string oldMember, out string rewrite) =>
        ModuleMemberConversions.TryGetValue((moduleName, oldMember), out rewrite!);

    public static bool TryMethod(string receiverKind, string oldMethod, out string replacement) =>
        Methods.TryGetValue((receiverKind, oldMethod), out replacement!);

    public static bool TryType(string oldType, out string replacement) => Types.TryGetValue(oldType, out replacement!);

    public static bool TryField(string typeName, string oldField, out string replacement) =>
        Fields.TryGetValue((typeName, oldField), out replacement!);

    public static bool TryParameter(string entryName, string oldParameter, out string replacement) =>
        Parameters.TryGetValue((entryName, oldParameter), out replacement!);
}
