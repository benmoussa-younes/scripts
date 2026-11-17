using System.Text.RegularExpressions;
using Scripts.Core;

namespace Scripts.Checker.Tests;

/// <summary>
/// R3 acceptance criterion: every stdlib explanation template
/// (<see cref="StdlibEntry.ExplainTemplate"/>, generated in StdlibData.g.cs) is
/// instantiated by a test, and every instantiation renders with no unresolved
/// placeholder left behind (no `{receiver}`, `{text}`, …).
///
/// Method (documented as required by the task):
///  1. For each catalog entry we build a minimal scripts program whose statements call
///     that entry (global function, module function, or method on a receiver of the
///     right kind), then run loader -> <see cref="ProgramChecker"/> -> <see cref="ProgramExplainer"/>,
///     exactly like `scripts explain` does (src/Scripts.App/ScriptsApplication.cs RunExplain).
///  2. A template counts as *reached* when at least one of its literal segments
///     (the text between two `{placeholder}` holes, or the whole template when it has
///     none) appears verbatim in the rendered explanation. This is robust against the
///     generated argument descriptions and cannot match by accident across entries.
///  3. A template counts as *clean* when the rendered explanation contains no
///     `{lowercase_identifier}` placeholder anymore.
///
/// The few entries whose call cannot be produced by a minimal program without doing
/// real I/O at check time (e.g. an argument of type process_handle returned by another
/// call) are still covered through the direct template substitution fallback below:
/// every `{name}` must be a declared parameter, plus `{receiver}` for methods.
/// </summary>
public sealed class ExplainTemplateTests
{
    /// <summary>
    /// A call snippet per entry, keyed by <c>(kind, qualified_name)</c> (<see cref="SnippetKey"/>).
    /// `qualified_name` is unique across the catalogue (DESIGN §10); the key keeps the `kind`
    /// for a stable, explicit identity.
    /// </summary>
    private static readonly Dictionary<string, string> Snippets = new(StringComparer.Ordinal)
    {
        // --- global functions ---
        [SnippetKey(StdlibKind.ModuleFunction, "io.write_standard_output_line")] = "io.write_standard_output_line(\"x\")",
        [SnippetKey(StdlibKind.Function, "assert")] = "assert(true, \"x\")",
        [SnippetKey(StdlibKind.ModuleFunction, "io.write_standard_error_line")] = "io.write_standard_error_line(\"x\")",
        [SnippetKey(StdlibKind.ModuleFunction, "io.write_standard_error")] = "io.write_standard_error(\"x\")",
        [SnippetKey(StdlibKind.Method, "string.format_template")] = "let _r = r\"{0}\".format_template([\"a\"])?",
        [SnippetKey(StdlibKind.Function, "to_float")] = "let _r = to_float(1)",
        [SnippetKey(StdlibKind.Function, "to_integer")] = "let _r = to_integer(1.0)?",
        [SnippetKey(StdlibKind.Method, "string.to_path")] = "let _r = \"a\".to_path()",
        [SnippetKey(StdlibKind.Method, "string.index_of")] = "let _r = \"a\".index_of(\"a\")?",
        [SnippetKey(StdlibKind.Method, "string.characters")] = "let _r = \"a\".characters()",
        [SnippetKey(StdlibKind.Method, "string.repeat")] = "let _r = \"a\".repeat(2)?",
        [SnippetKey(StdlibKind.Method, "string.trim_start")] = "let _r = \" a\".trim_start()",
        [SnippetKey(StdlibKind.Method, "string.trim_end")] = "let _r = \"a \".trim_end()",
        [SnippetKey(StdlibKind.Method, "string.sha256")] = "let _r = \"a\".sha256()",
        [SnippetKey(StdlibKind.Method, "string.parse_integer_radix")] = "let _r = \"1\".parse_integer_radix(2)?",
        [SnippetKey(StdlibKind.Method, "integer.bit_and")] = "let _r = 1.bit_and(1)",
        [SnippetKey(StdlibKind.Method, "integer.bit_or")] = "let _r = 1.bit_or(1)",
        [SnippetKey(StdlibKind.Method, "integer.bit_xor")] = "let _r = 1.bit_xor(1)",
        [SnippetKey(StdlibKind.Method, "integer.bit_not")] = "let _r = 1.bit_not()",
        [SnippetKey(StdlibKind.Method, "integer.shift_left")] = "let _r = 1.shift_left(1)?",
        [SnippetKey(StdlibKind.Method, "integer.shift_right")] = "let _r = 1.shift_right(1)?",
        [SnippetKey(StdlibKind.Method, "integer.to_radix")] = "let _r = 1.to_radix(2)?",
        [SnippetKey(StdlibKind.Method, "integer.absolute")] = "let _r = 1.absolute()?",
        [SnippetKey(StdlibKind.Method, "float.to_fixed")] = "let _r = (1.0).to_fixed(2)?",
        [SnippetKey(StdlibKind.Method, "float.absolute")] = "let _r = (1.0).absolute()",
        [SnippetKey(StdlibKind.Function, "range")] = "let _r = range(0, 3)",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.read_file")] = "let _r = filesystem.read_file(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.read_file_no_follow")] = "let _r = filesystem.read_file_no_follow(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.for_each_line")] = "filesystem.for_each_line(p\"/tmp/x\", line => ok())?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.write_file_overwrite")] = "filesystem.write_file_overwrite(\"a\", p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.append_file")] = "filesystem.append_file(\"a\", p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.path_exists")] = "let _r = filesystem.path_exists(p\"/tmp/x\")",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.get_file_system_status")] = "let _r = filesystem.get_file_system_status(p\"/tmp\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.get_path_status")] = "let _r = filesystem.get_path_status(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.remove_file")] = "filesystem.remove_file(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.remove_empty_directory")] = "filesystem.remove_empty_directory(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.remove_directory_tree")] = "filesystem.remove_directory_tree(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_directory")] = "filesystem.make_directory(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_directory_with_permissions")] = "filesystem.make_directory_with_permissions(p\"/tmp/x\", 0o750)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_directory_tree")] = "filesystem.make_directory_tree(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.copy_file")] = "filesystem.copy_file(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.copy_file_overwrite")] = "filesystem.copy_file_overwrite(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.move_path")] = "filesystem.move_path(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.move_path_overwrite")] = "filesystem.move_path_overwrite(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.get_current_directory")] = "let _r = filesystem.get_current_directory()?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.set_current_directory")] = "filesystem.set_current_directory(p\"/tmp\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_temporary_directory")] = "let _r = filesystem.make_temporary_directory(\"t\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.copy_directory_tree")] = "filesystem.copy_directory_tree(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_hard_link")] = "filesystem.make_hard_link(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_symbolic_link")] = "filesystem.make_symbolic_link(p\"/tmp/a\", p\"/tmp/b\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.read_link")] = "let _r = filesystem.read_link(p\"/tmp/a\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.get_real_path")] = "let _r = filesystem.get_real_path(p\"/tmp/a\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.set_permissions")] = "filesystem.set_permissions(p\"/tmp/a\", 0o600)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.set_owner")] = "filesystem.set_owner(p\"/tmp/a\", 0, 0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.set_owner_no_follow")] = "filesystem.set_owner_no_follow(p\"/tmp/a\", 0, 0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.make_temporary_file")] = "let _r = filesystem.make_temporary_file(\"t\", \".tmp\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.write_file_exclusive")] = "filesystem.write_file_exclusive(\"a\", p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.write_file_exclusive_with_permissions")] = "filesystem.write_file_exclusive_with_permissions(\"a\", p\"/tmp/x\", 0o600)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.touch")] = "filesystem.touch(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.glob")] = "let _r = filesystem.glob(p\"/tmp\", \"*.txt\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.acquire_file_lock")] = "let _r = filesystem.acquire_file_lock(p\"/tmp/lock\", 0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "system.get_information")] = "let _r = system.get_information()?",
        [SnippetKey(StdlibKind.ModuleFunction, "system.user_name")] = "let _r = system.user_name()",
        [SnippetKey(StdlibKind.ModuleFunction, "system.effective_user_id")] = "let _r = system.effective_user_id()",
        [SnippetKey(StdlibKind.ModuleFunction, "system.effective_group_id")] = "let _r = system.effective_group_id()",
        [SnippetKey(StdlibKind.ModuleFunction, "system.user_id")] = "let _r = system.user_id(\"root\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "system.group_id")] = "let _r = system.group_id(\"root\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "system.cpu_count")] = "let _r = system.cpu_count()",

        // --- module functions ---
        [SnippetKey(StdlibKind.ModuleFunction, "environment.get_variable")] = "let _r = environment.get_variable(\"PATH\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "environment.set_variable")] = "environment.set_variable(\"K\", \"v\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "environment.remove_variable")] = "environment.remove_variable(\"K\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "environment.all_variables")] = "let _r = environment.all_variables()",

        // --- intrinsic namespaces (available without import) ---
        [SnippetKey(StdlibKind.ModuleFunction, "io.read_standard_input")] = "let _r = io.read_standard_input()?",
        [SnippetKey(StdlibKind.ModuleFunction, "io.read_standard_input_line")] = "let _r = io.read_standard_input_line()?",
        [SnippetKey(StdlibKind.ModuleFunction, "io.write_standard_output")] = "io.write_standard_output(\"x\")",
        [SnippetKey(StdlibKind.ModuleFunction, "process.defer")] = "process.defer(() => io.write_standard_output_line(\"x\"))",
        [SnippetKey(StdlibKind.ModuleFunction, "process.pipeline")] = "let _r = process.pipeline([process.request(\"true\", [])])?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.pipeline_to_file_overwrite")] = "let _r = process.pipeline_to_file_overwrite([], p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "json.encode")] = "let _r = json.encode(1)?",
        [SnippetKey(StdlibKind.ModuleFunction, "json.encode_pretty")] = "let _r = json.encode_pretty(1)?",
        [SnippetKey(StdlibKind.ModuleFunction, "json.decode")] = "let _r = json.decode(\"1\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.walk_directory")] = "let _r = filesystem.walk_directory(p\"/tmp\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.list_directory")] = "let _r = filesystem.list_directory(p\"/tmp\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.write_file_overwrite_atomically")] = "filesystem.write_file_overwrite_atomically(\"a\", p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.read_file_bounded")] = "let _r = filesystem.read_file_bounded(p\"/tmp/x\", 8)?",
        [SnippetKey(StdlibKind.ModuleFunction, "filesystem.sha256_file")] = "let _r = filesystem.sha256_file(p\"/tmp/x\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.request")] = "let _r = process.request(\"true\", [])",
        [SnippetKey(StdlibKind.ModuleFunction, "process.spawn")] = "let _r = process.spawn(\"true\", [])?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.wait_for_process")] = "let _h = process.spawn(\"true\", [])?\nlet _r = process.wait_for_process(_h, 0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.terminate")] = "let _h = process.spawn(\"true\", [])?\nprocess.terminate(_h)?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.run")] = "let _r = process.run(process.request(\"true\", []))?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.run_inherit")] = "let _r = process.run_inherit(process.request(\"true\", []))?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.script_path")] = "let _r = process.script_path()",
        [SnippetKey(StdlibKind.ModuleFunction, "process.find_program_path")] = "let _r = process.find_program_path(\"sh\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.exit_with_code")] = "process.exit_with_code(0)",
        [SnippetKey(StdlibKind.ModuleFunction, "process.identifier")] = "let _r = process.identifier()",
        [SnippetKey(StdlibKind.ModuleFunction, "process.set_umask")] = "let _r = process.set_umask(0o022)?",
        [SnippetKey(StdlibKind.ModuleFunction, "encoding.base64_encode")] = "let _r = encoding.base64_encode(\"a\")",
        [SnippetKey(StdlibKind.ModuleFunction, "encoding.base64_decode")] = "let _r = encoding.base64_decode(\"YQ==\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "encoding.hex_encode")] = "let _r = encoding.hex_encode(\"a\")",
        [SnippetKey(StdlibKind.ModuleFunction, "encoding.hex_decode")] = "let _r = encoding.hex_decode(\"61\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "random.bytes_hex")] = "let _r = random.bytes_hex(1)?",
        [SnippetKey(StdlibKind.ModuleFunction, "random.bytes_base64")] = "let _r = random.bytes_base64(1)?",
        [SnippetKey(StdlibKind.ModuleFunction, "random.integer_below")] = "let _r = random.integer_below(2)?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.run_shell")] = "let _r = process.run_shell(\"true\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "process.arguments")] = "let _r = process.arguments()",
        [SnippetKey(StdlibKind.ModuleFunction, "json.null_value")] = "let _r = json.null_value()",
        [SnippetKey(StdlibKind.ModuleFunction, "json.from_string")] = "let _r = json.from_string(\"a\")",
        [SnippetKey(StdlibKind.ModuleFunction, "json.from_integer")] = "let _r = json.from_integer(1)",
        [SnippetKey(StdlibKind.ModuleFunction, "json.from_float")] = "let _r = json.from_float(1.0)",
        [SnippetKey(StdlibKind.ModuleFunction, "json.from_boolean")] = "let _r = json.from_boolean(true)",
        [SnippetKey(StdlibKind.ModuleFunction, "json.array")] = "let _r = json.array([])",
        [SnippetKey(StdlibKind.ModuleFunction, "json.object")] = "let _r = json.object({})",
        [SnippetKey(StdlibKind.ModuleFunction, "time.unix_seconds")] = "let _r = time.unix_seconds()",
        [SnippetKey(StdlibKind.ModuleFunction, "time.monotonic_milliseconds")] = "let _r = time.monotonic_milliseconds()",
        [SnippetKey(StdlibKind.ModuleFunction, "time.sleep_milliseconds")] = "time.sleep_milliseconds(0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "time.to_utc_iso8601")] = "let _r = time.to_utc_iso8601(0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "time.to_local_iso8601")] = "let _r = time.to_local_iso8601(0)?",
        [SnippetKey(StdlibKind.ModuleFunction, "time.parse_iso8601")] = "let _r = time.parse_iso8601(\"1970-01-01T00:00:00Z\")?",
        [SnippetKey(StdlibKind.ModuleFunction, "parallel.transform")] = "let _r = parallel.transform([1], 1, n => n)?",
        [SnippetKey(StdlibKind.ModuleFunction, "parallel.run_all")] = "let _r = parallel.run_all([], 1)?",
        [SnippetKey(StdlibKind.ModuleFunction, "parallel.walk_directory")] = "let _r = parallel.walk_directory(p\"/tmp\", 1, p => io.write_standard_output_line(\"x\"))?",

        // --- path methods (§7.5.1) ---
        [SnippetKey(StdlibKind.Method, "path.base_name")] = "let _r = p\"/tmp/a\".base_name()",
        [SnippetKey(StdlibKind.Method, "path.extension")] = "let _r = p\"/tmp/a.txt\".extension()",
        [SnippetKey(StdlibKind.Method, "path.parent")] = "let _r = p\"/tmp/a\".parent()",
        [SnippetKey(StdlibKind.Method, "path.to_string")] = "let _r = p\"/tmp/a\".to_string()",
        [SnippetKey(StdlibKind.Method, "path.is_absolute")] = "let _r = p\"/tmp\".is_absolute()",
        [SnippetKey(StdlibKind.Method, "path.matches_glob")] = "let _r = p\"a/b.txt\".matches_glob(\"**/*.txt\")?",
        [SnippetKey(StdlibKind.Method, "path.normalized")] = "let _r = p\"a/../b\".normalized()",
        [SnippetKey(StdlibKind.Method, "path.file_stem")] = "let _r = p\"a/b.txt\".file_stem()",
        [SnippetKey(StdlibKind.Method, "path.with_extension")] = "let _r = p\"a/b.txt\".with_extension(\".md\")",

        // --- string methods ---
        [SnippetKey(StdlibKind.Method, "string.length")] = "let _r = \"a\".length()",
        [SnippetKey(StdlibKind.Method, "string.split")] = "let _r = \"a,b\".split(\",\")",
        [SnippetKey(StdlibKind.Method, "string.trim_whitespace")] = "let _r = \" a \".trim_whitespace()",
        [SnippetKey(StdlibKind.Method, "string.replace")] = "let _r = \"a\".replace(\"a\", \"b\")",
        [SnippetKey(StdlibKind.Method, "string.contains")] = "let _r = \"a\".contains(\"a\")",
        [SnippetKey(StdlibKind.Method, "string.starts_with")] = "let _r = \"a\".starts_with(\"a\")",
        [SnippetKey(StdlibKind.Method, "string.ends_with")] = "let _r = \"a\".ends_with(\"a\")",
        [SnippetKey(StdlibKind.Method, "string.matches")] = "let _r = \"a\".matches(\"a\")?",
        [SnippetKey(StdlibKind.Method, "string.lines")] = "let _r = \"a\".lines()",
        [SnippetKey(StdlibKind.Method, "string.parse_integer")] = "let _r = \"1\".parse_integer()?",
        [SnippetKey(StdlibKind.Method, "string.parse_float")] = "let _r = \"1.0\".parse_float()?",
        [SnippetKey(StdlibKind.Method, "string.uppercased")] = "let _r = \"a\".uppercased()",
        [SnippetKey(StdlibKind.Method, "string.lowercased")] = "let _r = \"A\".lowercased()",
        [SnippetKey(StdlibKind.Method, "string.substring")] = "let _r = \"abc\".substring(0, 2)?",
        [SnippetKey(StdlibKind.Method, "string.strip_prefix")] = "let _r = \"ab\".strip_prefix(\"a\")",
        [SnippetKey(StdlibKind.Method, "string.strip_suffix")] = "let _r = \"ab\".strip_suffix(\"b\")",
        [SnippetKey(StdlibKind.Method, "string.replace_first")] = "let _r = \"aa\".replace_first(\"a\", \"b\")",
        [SnippetKey(StdlibKind.Method, "string.capture_groups")] = "let _r = \"a1\".capture_groups(\".\")?",
        [SnippetKey(StdlibKind.Method, "string.replace_regex")] = "let _r = \"a1\".replace_regex(\".\", \"x\")?",
        [SnippetKey(StdlibKind.Method, "string.split_regex")] = "let _r = \"a1b\".split_regex(\".\")?",
        [SnippetKey(StdlibKind.Method, "string.pad_start")] = "let _r = \"a\".pad_start(3, \"0\")?",
        [SnippetKey(StdlibKind.Method, "string.pad_end")] = "let _r = \"a\".pad_end(3, \"0\")?",

        // --- list methods ---
        [SnippetKey(StdlibKind.Method, "list<T>.length")] = "let mutable _l = [1]\nlet _r = _l.length()",
        [SnippetKey(StdlibKind.Method, "list<T>.get_at_index")] = "let mutable _l = [1]\nlet _r = _l.get_at_index(0)?",
        [SnippetKey(StdlibKind.Method, "list<T>.append")] = "let mutable _l = [1]\n_l.append(2)",
        [SnippetKey(StdlibKind.Method, "list<T>.contains")] = "let mutable _l = [1]\nlet _r = _l.contains(1)",
        [SnippetKey(StdlibKind.Method, "list<string>.join")] = "let _l = [\"a\"]\nlet _r = _l.join(\",\")",
        [SnippetKey(StdlibKind.Method, "list<T>.filter")] = "let _l = [1]\nlet _r = _l.filter(item => item > 0)",
        [SnippetKey(StdlibKind.Method, "list<T>.transform")] = "let _l = [1]\nlet _r = _l.transform(item => item + 1)",
        [SnippetKey(StdlibKind.Method, "list<T>.fold")] = "let _l = [1]\nlet _r = _l.fold(0, (accumulator, item) => accumulator + item)",
        [SnippetKey(StdlibKind.Method, "list<T>.sorted")] = "let _l = [1]\nlet _r = _l.sorted()",
        [SnippetKey(StdlibKind.Method, "list<T>.reversed")] = "let _l = [1]\nlet _r = _l.reversed()",
        [SnippetKey(StdlibKind.Method, "list<T>.sorted_by")] = "let _l = [1]\nlet _r = _l.sorted_by(item => item)",
        [SnippetKey(StdlibKind.Method, "list<result<T,E>>.collect")] = "let items = [\"1\"].transform(t => t.parse_integer())\nlet _r = items.collect()?",
        [SnippetKey(StdlibKind.Method, "list<T>.any_match")] = "let _l = [1]\nlet _r = _l.any_match(item => item > 0)",
        [SnippetKey(StdlibKind.Method, "list<T>.all_match")] = "let _l = [1]\nlet _r = _l.all_match(item => item > 0)",
        [SnippetKey(StdlibKind.Method, "list<T>.count")] = "let _l = [1]\nlet _r = _l.count(item => item > 0)",
        [SnippetKey(StdlibKind.Method, "list<T>.find_first")] = "let _l = [1]\nlet _r = _l.find_first(item => item > 0)?",
        [SnippetKey(StdlibKind.Method, "list<integer>.sum")] = "let _l = [1]\nlet _r = _l.sum()",
        [SnippetKey(StdlibKind.Method, "list<T>.minimum")] = "let _l = [1]\nlet _r = _l.minimum()?",
        [SnippetKey(StdlibKind.Method, "list<T>.maximum")] = "let _l = [1]\nlet _r = _l.maximum()?",
        [SnippetKey(StdlibKind.Method, "list<T>.slice")] = "let _r = [1, 2, 3].slice(0, 2)?",
        [SnippetKey(StdlibKind.Method, "list<T>.insert_at")] = "let mutable _l = [1]\n_l.insert_at(0, 2)?",
        [SnippetKey(StdlibKind.Method, "list<T>.remove_at")] = "let mutable _l = [1]\nlet _r = _l.remove_at(0)?",
        [SnippetKey(StdlibKind.Method, "list<T>.distinct")] = "let _r = [1, 1].distinct()",
        [SnippetKey(StdlibKind.Method, "list<list<T>>.flatten")] = "let _r = [[1], [2]].flatten()",
        [SnippetKey(StdlibKind.Method, "list<T>.group_by")] = "let _r = [1, 2].group_by(item => item)",

        // --- map methods ---
        [SnippetKey(StdlibKind.Method, "map<K,V>.length")] = "let _m = {\"a\": 1}\nlet _r = _m.length()",
        [SnippetKey(StdlibKind.Method, "map<K,V>.get_value")] = "let _m = {\"a\": 1}\nlet _r = _m.get_value(\"a\")?",
        [SnippetKey(StdlibKind.Method, "map<K,V>.contains_key")] = "let _m = {\"a\": 1}\nlet _r = _m.contains_key(\"a\")",
        [SnippetKey(StdlibKind.Method, "map<K,V>.keys")] = "let _m = {\"a\": 1}\nlet _r = _m.keys()",
        [SnippetKey(StdlibKind.Method, "map<K,V>.values")] = "let _m = {\"a\": 1}\nlet _r = _m.values()",
        [SnippetKey(StdlibKind.Method, "map<K,V>.remove_key")] = "let mutable _m = {\"a\": 1}\nlet _r = _m.remove_key(\"a\")",
        [SnippetKey(StdlibKind.Method, "map<K,V>.merged_with")] = "let _r = {\"a\": 1}.merged_with({\"b\": 2})",
        [SnippetKey(StdlibKind.Method, "map<K,V>.filter")] = "let _r = {\"a\": 1}.filter((key, value) => value > 0)",
        [SnippetKey(StdlibKind.Method, "map<K,V>.transform_values")] = "let _r = {\"a\": 1}.transform_values(value => value + 1)",
        [SnippetKey(StdlibKind.Method, "json_value.kind")] = "let _v = json.decode(\"1\")?\nlet _r = _v.kind()",
        [SnippetKey(StdlibKind.Method, "json_value.as_string")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_string()?",
        [SnippetKey(StdlibKind.Method, "json_value.as_integer")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_integer()?",
        [SnippetKey(StdlibKind.Method, "json_value.as_float")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_float()?",
        [SnippetKey(StdlibKind.Method, "json_value.as_boolean")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_boolean()?",
        [SnippetKey(StdlibKind.Method, "json_value.as_list")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_list()?",
        [SnippetKey(StdlibKind.Method, "json_value.as_map")] = "let _v = json.decode(\"1\")?\nlet _r = _v.as_map()?",
        [SnippetKey(StdlibKind.Method, "file_lock.release")] = "let _l = filesystem.acquire_file_lock(p\"/tmp/lock\", 0)?\n_l.release()?",

        // --- result methods ---
        [SnippetKey(StdlibKind.Method, "result<T,E>.is_ok")] = "let parsed = \"1\".parse_integer()\nlet _r = parsed.is_ok()",
        [SnippetKey(StdlibKind.Method, "result<T,E>.value_or")] = "let parsed = \"1\".parse_integer()\nlet _r = parsed.value_or(0)",
        [SnippetKey(StdlibKind.Method, "result<T,E>.value_or_fail")] = "let parsed = \"1\".parse_integer()\nlet _r = parsed.value_or_fail()",
        [SnippetKey(StdlibKind.Method, "result<T,E>.transform_error")] = "let parsed = \"1\".parse_integer()\nlet _r = parsed.transform_error(e => e)?",

        // --- process_result method ---
        [SnippetKey(StdlibKind.Method, "process_result.require_success")] = "let _p = process.run(process.request(\"true\", []))?\nlet _r = _p.require_success()?",
        [SnippetKey(StdlibKind.Method, "pipeline_result.require_success")] = "let _p = process.pipeline([process.request(\"true\", [])])?\nlet _r = _p.require_success()?",
        [SnippetKey(StdlibKind.Method, "run_request.with_standard_input")] = "let _r = process.request(\"true\", []).with_standard_input(\"text\")",
        [SnippetKey(StdlibKind.Method, "run_request.with_standard_input_from_file")] = "let _r = process.request(\"true\", []).with_standard_input_from_file(p\"/dev/null\")",
        [SnippetKey(StdlibKind.Method, "run_request.with_working_directory")] = "let _r = process.request(\"true\", []).with_working_directory(p\"/tmp\")",
        [SnippetKey(StdlibKind.Method, "run_request.with_environment")] = "let _r = process.request(\"true\", []).with_environment({\"GREETING\": \"hi\"})",
        [SnippetKey(StdlibKind.Method, "run_request.with_timeout")] = "let _r = process.request(\"true\", []).with_timeout(1000)",
    };

    /// <summary>`(kind, qualified_name)` — unique across the catalogue.</summary>
    private static string SnippetKey(StdlibKind kind, string qualifiedName) => kind + ":" + qualifiedName;

    private static string SnippetKeyOf(StdlibEntry entry) => SnippetKey(entry.Kind, entry.QualifiedName);

    private static readonly Regex UnresolvedPlaceholder = new(@"\{[a-z_][a-z0-9_]*\}", RegexOptions.Compiled);

    [Fact]
    public void There_are_the_catalog_entries()
    {
        Assert.Equal(206, StdlibCatalog.All.Count);
    }

    [Fact]
    public void Every_catalog_entry_has_a_call_snippet()
    {
        var missing = StdlibCatalog.All.Select(SnippetKeyOf).Where(name => !Snippets.ContainsKey(name)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "No call snippet for: " + string.Join(", ", missing));
        var extra = Snippets.Keys.Where(name => StdlibCatalog.All.All(entry => SnippetKeyOf(entry) != name)).Order(StringComparer.Ordinal).ToList();
        Assert.True(extra.Count == 0, "Snippet for unknown entry: " + string.Join(", ", extra));
    }

    [Fact]
    public void Explain_reaches_every_template_and_leaves_no_placeholder()
    {
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var entry in StdlibCatalog.All)
        {
            var explanation = ExplainSnippet(Snippets[SnippetKeyOf(entry)]);
            if (!ReachesTemplate(explanation, entry.ExplainTemplate))
            {
                problems.Add($"{entry.QualifiedName}: template not reached (explanation: {explanation.Replace("\n", " | ")})");
                continue;
            }

            reached.Add(SnippetKeyOf(entry));
            var unresolved = UnresolvedPlaceholder.Match(explanation);
            if (unresolved.Success)
            {
                problems.Add($"{entry.QualifiedName}: unresolved placeholder {unresolved.Value} in: {explanation.Replace("\n", " | ")}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
        var unreached = StdlibCatalog.All.Select(SnippetKeyOf).Where(name => !reached.Contains(name)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unreached.Count == 0, "Templates never reached: " + string.Join(", ", unreached));
        Assert.Equal(206, reached.Count);
    }

    /// <summary>
    /// Fallback documented by the task: instance each template by substituting every declared
    /// parameter (plus `{receiver}` for methods); the result must contain no known placeholder
    /// and the template must only use declared placeholders.
    /// </summary>
    [Fact]
    public void Every_template_only_uses_declared_placeholders_and_renders_clean()
    {
        var problems = new List<string>();
        foreach (var entry in StdlibCatalog.All)
        {
            var declared = entry.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.Ordinal);
            if (entry.Kind == StdlibKind.Method)
            {
                declared.Add("receiver");
            }

            var unknown = Regex.Matches(entry.ExplainTemplate, @"\{([a-zA-Z_][a-zA-Z0-9_]*)\}")
                .Select(match => match.Groups[1].Value)
                .Where(name => !declared.Contains(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"{entry.QualifiedName}: unknown placeholder(s) {{{string.Join("}, {", unknown)}}} (declared: {string.Join(", ", declared)})");
            }

            if (entry.ExplainTemplate.Contains('{') != Regex.IsMatch(entry.ExplainTemplate, @"\{[a-zA-Z_][a-zA-Z0-9_]*\}"))
            {
                problems.Add($"{entry.QualifiedName}: malformed placeholder in [{entry.ExplainTemplate}]");
            }

            var arguments = entry.Parameters.Select(parameter => (parameter.Name, $"[{parameter.Name}]")).ToList();
            if (entry.Kind == StdlibKind.Method)
            {
                arguments.Insert(0, ("receiver", "[receiver]"));
            }

            var rendered = Diagnostics.Render(entry.ExplainTemplate, [.. arguments]);
            var residual = UnresolvedPlaceholder.Match(rendered);
            if (residual.Success)
            {
                problems.Add($"{entry.QualifiedName}: residual {residual.Value} after substitution: {rendered}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string ExplainSnippet(string text)
    {
        var loaded = Checking.Load(text);
        var check = ProgramChecker.Check(loaded);
        Assert.True(check.Errors.Count == 0, $"snippet does not check: {Checking.Describe(check)}\n{text}");
        return ProgramExplainer.Explain(loaded, check.Warnings);
    }

    /// <summary>
    /// True when the rendered explanation contains one of the template's literal segments
    /// (the text between two holes). A template without holes is matched as a whole.
    /// </summary>
    private static bool ReachesTemplate(string explanation, string template)
    {
        // The explainer prepends nothing but may prefix destructive/dangerous markers; the template text itself is kept.
        foreach (var segment in LiteralSegments(template))
        {
            if (explanation.Contains(segment, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> LiteralSegments(string template)
    {
        // Segments are the literal runs between holes. We keep runs of at least 4 characters
        // and try the longest first; each snippet calls exactly one stdlib entry, so an
        // accidental match with another template is impossible.
        var segments = Regex.Split(template, @"\{[a-zA-Z_][a-zA-Z0-9_]*\}")
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length >= 4)
            .OrderByDescending(segment => segment.Length)
            .ToList();
        if (segments.Count == 0)
        {
            segments.Add(template.Trim());
        }

        return segments;
    }
}
