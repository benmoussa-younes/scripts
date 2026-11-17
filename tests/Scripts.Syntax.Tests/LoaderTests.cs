using System.Text;
using Scripts.Core;

namespace Scripts.Syntax.Tests;

public sealed class LoaderTests
{
    private static InMemorySourceProvider Provider(params (string Path, string Text)[] files)
    {
        var provider = new InMemorySourceProvider();
        foreach (var (path, text) in files)
        {
            provider.AddText(path, text);
        }

        return provider;
    }

    private static LoadedProgram LoadOk(string entry, InMemorySourceProvider provider)
    {
        var result = ProgramLoader.Load(entry, provider);
        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        return result.Program!;
    }

    private static Diagnostic LoadFail(string entry, InMemorySourceProvider provider)
    {
        var result = ProgramLoader.Load(entry, provider);
        Assert.Null(result.Program);
        return Assert.IsType<Diagnostic>(result.Error);
    }

    /// <summary>
    /// An in-memory provider whose root is a current directory: a relative path (such as the import of the
    /// standard-input entry) resolves against <paramref name="currentDirectory"/>, like a file system provider.
    /// </summary>
    private sealed class CurrentDirectorySourceProvider(InMemorySourceProvider inner, string currentDirectory) : ISourceProvider
    {
        public string Canonicalize(string path) => inner.Canonicalize(path.StartsWith('/') ? path : currentDirectory + "/" + path);

        public bool TryReadBytes(string path, out byte[] bytes, out string error) => inner.TryReadBytes(Canonicalize(path), out bytes, out error);
    }

    [Fact]
    public void A_script_without_imports_loads_alone()
    {
        var program = LoadOk("/main.scs", Provider(("/main.scs", "io.write_standard_output_line(\"a\")\n")));
        Assert.Empty(program.Modules);
        Assert.False(program.Entry.IsModule);
        Assert.Equal("/main.scs", program.Entry.CanonicalPath);
        Assert.Equal("/main.scs", program.Entry.Source.Path);
        Assert.Empty(program.Entry.Imports);
    }

    [Fact]
    public void Modules_are_loaded_depth_first_with_dependencies_before_importers()
    {
        var program = LoadOk(
            "/main.scs",
            Provider(
                ("/main.scs", "import \"a.scs\" as a\nimport \"b.scs\" as b\nio.write_standard_output_line(\"x\")\n"),
                ("/a.scs", "import \"c.scs\" as c\npublic let one = 1\n"),
                ("/b.scs", "import \"c.scs\" as shared\npublic let two = 2\n"),
                ("/c.scs", "public let three = 3\n")));
        Assert.Equal(["/c.scs", "/a.scs", "/b.scs"], program.Modules.Select(module => module.CanonicalPath));
        Assert.Equal(["/c.scs", "/a.scs", "/b.scs", "/main.scs"], program.AllFilesInLoadOrder().Select(file => file.CanonicalPath));
        Assert.All(program.Modules, module => Assert.True(module.IsModule));
    }

    [Fact]
    public void A_module_imported_twice_is_loaded_once_and_shared()
    {
        var program = LoadOk(
            "/main.scs",
            Provider(
                ("/main.scs", "import \"a.scs\" as a\nimport \"b.scs\" as b\n"),
                ("/a.scs", "import \"c.scs\" as c\npublic let one = 1\n"),
                ("/b.scs", "import \"c.scs\" as shared\npublic let two = 2\n"),
                ("/c.scs", "public let three = 3\n")));
        var fromA = program.Modules.Single(module => module.CanonicalPath == "/a.scs").Imports["c"];
        var fromB = program.Modules.Single(module => module.CanonicalPath == "/b.scs").Imports["shared"];
        Assert.Same(fromA, fromB);
        Assert.Equal(3, program.Modules.Count);
        Assert.Equal(["a", "b"], program.Entry.Imports.Keys.Order());
    }

    [Fact]
    public void Different_spellings_of_the_same_path_share_one_identity()
    {
        var program = LoadOk(
            "/main.scs",
            Provider(
                ("/main.scs", "import \"lib/x.scs\" as first\nimport \"./lib/../lib/x.scs\" as second\n"),
                ("/lib/x.scs", "public let v = 1\n")));
        Assert.Single(program.Modules);
        Assert.Same(program.Entry.Imports["first"], program.Entry.Imports["second"]);
    }

    [Fact]
    public void Import_paths_are_relative_to_the_importing_file()
    {
        var program = LoadOk(
            "/project/main.scs",
            Provider(
                ("/project/main.scs", "import \"sub/a.scs\" as a\n"),
                ("/project/sub/a.scs", "import \"b.scs\" as b\nimport \"../top.scs\" as top\npublic let x = 1\n"),
                ("/project/sub/b.scs", "public let y = 2\n"),
                ("/project/top.scs", "public let z = 3\n")));
        Assert.Equal(["/project/sub/b.scs", "/project/top.scs", "/project/sub/a.scs"], program.Modules.Select(module => module.CanonicalPath));
    }

    [Fact]
    public void A_relative_entry_path_resolves_modules_next_to_it()
    {
        var program = LoadOk("main.scs", Provider(("main.scs", "import \"lib.scs\" as lib\n"), ("lib.scs", "public let v = 1\n")));
        Assert.Equal("main.scs", program.Entry.Source.Path);
        Assert.Equal("lib.scs", program.Modules.Single().Source.Path);
    }

    [Fact]
    public void Importing_an_intrinsic_namespace_is_rejected()
    {
        // The standard modules are intrinsic (DESIGN §1.5): `std:json` is not an importable
        // module and the message says so.
        var result = ProgramLoader.Load("/main.scs", Provider(("/main.scs", "import \"std:json\" as json\n")));

        Assert.Null(result.Program);
        var error = Assert.IsType<Diagnostic>(result.Error);
        Assert.Equal(ErrorCodes.Module0005, error.Code);
        Assert.Contains("intrinsic", error.Message, StringComparison.Ordinal);
        Assert.Contains("std:json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bcs_extension_is_optional_but_the_alias_is_explicit()
    {
        var program = LoadOk(
            "/project/main.scs",
            Provider(
                ("/project/main.scs", "import \"lib/numbers\" as numbers\nimport \"tools.scs\" as tools\n"),
                ("/project/lib/numbers.scs", "public let one = 1\n"),
                ("/project/tools.scs", "public let two = 2\n")));

        Assert.Equal(["/project/lib/numbers.scs", "/project/tools.scs"], program.Modules.Select(module => module.CanonicalPath).Order());
        Assert.Equal(["numbers", "tools"], program.Entry.Imports.Keys.Order());
    }

    [Fact]
    public void An_explicit_alias_overrides_the_default()
    {
        var program = LoadOk(
            "/main.scs",
            Provider(("/main.scs", "import \"numbers\" as n\n"), ("/numbers.scs", "public let one = 1\n")));

        Assert.Equal("n", Assert.Single(program.Entry.Imports.Keys));
    }

    [Fact]
    public void Imports_require_an_explicit_alias()
    {
        var missingAlias = LoadFail(
            "/main.scs",
            Provider(("/main.scs", "import \"lib/my-numbers.scs\"\n"), ("/lib/my-numbers.scs", "public let one = 1\n")));
        Assert.Equal(ErrorCodes.Module0007, missingAlias.Code);
        Assert.Contains("as", missingAlias.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modules_are_parsed_with_the_module_rules()
    {
        var program = LoadOk("/main.scs", Provider(("/main.scs", "import \"lib.scs\" as lib\n"), ("/lib.scs", "let v = 1\n")));
        var declaration = Assert.IsType<LetStatement>(program.Modules.Single().Program.Statements[0]);
        Assert.Equal(Visibility.Private, declaration.Visibility);
    }

    [Fact]
    public void Statements_in_a_module_are_left_for_the_checker()
    {
        var program = LoadOk("/main.scs", Provider(("/main.scs", "import \"lib.scs\" as lib\n"), ("/lib.scs", "io.write_standard_output_line(\"x\")\npublic let mutable v = f()\n")));
        Assert.Equal(2, program.Modules.Single().Program.Statements.Count);
    }

    [Fact]
    public void The_import_header_is_processed_before_the_rest_of_the_importing_file_is_parsed()
    {
        var provider = Provider(("/main.scs", "import \"absent.scs\" as a\nlet x = )\n"));
        Assert.Equal(ErrorCodes.Module0001, LoadFail("/main.scs", provider).Code);
    }

    [Fact]
    public void A_module_syntax_error_comes_before_a_body_error_of_the_importer()
    {
        var provider = Provider(("/main.scs", "import \"lib.scs\" as lib\nlet x = )\n"), ("/lib.scs", "public let v = )\n"));
        var error = LoadFail("/main.scs", provider);
        Assert.Equal((ErrorCodes.Parse0023, "lib.scs"), (error.Code, Path.GetFileName(error.FilePath)));
    }

    [Fact]
    public void A_header_error_in_the_importer_comes_before_any_module_is_loaded()
    {
        var provider = Provider(("/main.scs", "import p\"lib.scs\" as lib\nimport \"absent.scs\" as a\n"));
        Assert.Equal(ErrorCodes.Parse0052, LoadFail("/main.scs", provider).Code);
    }

    [Fact]
    public void Loading_stops_at_the_first_import_failure_in_import_order()
    {
        var provider = Provider(("/main.scs", "import \"first.scs\" as a\nimport \"second.scs\" as b\n"), ("/first.scs", "public let v = )\n"), ("/second.scs", "public let w = )\n"));
        Assert.Equal("first.scs", Path.GetFileName(LoadFail("/main.scs", provider).FilePath));
    }

    [Theory]
    [InlineData("std:yaml", ErrorCodes.Module0005)]
    [InlineData("std:export", ErrorCodes.Module0005)]
    [InlineData("std:encoding", ErrorCodes.Module0005)]
    [InlineData("std:random", ErrorCodes.Module0005)]
    [InlineData("std:", ErrorCodes.Module0005)]
    [InlineData("", ErrorCodes.Module0007)]
    [InlineData("/abs/lib.scs", ErrorCodes.Module0007)]
    [InlineData("lib.txt", ErrorCodes.Module0001)]
    [InlineData("lib", ErrorCodes.Module0001)]
    [InlineData("lib.scs.bak", ErrorCodes.Module0001)]
    [InlineData("nope.scs", ErrorCodes.Module0001)]
    public void Import_path_rules(string importPath, string expectedCode)
    {
        var provider = Provider(("/main.scs", $"import \"{importPath}\" as lib\n"));
        var error = LoadFail("/main.scs", provider);
        Assert.Equal((expectedCode, 1, 8), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void An_import_path_with_a_nul_character_is_invalid()
    {
        var provider = Provider(("/main.scs", "import \"a\\0b.scs\" as lib\n"));
        Assert.Equal(ErrorCodes.Module0007, LoadFail("/main.scs", provider).Code);
    }

    [Fact]
    public void Import_diagnostics_name_the_path_and_reason()
    {
        var missing = LoadFail("/dir/main.scs", Provider(("/dir/main.scs", "import \"absent.scs\" as lib\n")));
        Assert.Contains("absent.scs", missing.Message, StringComparison.Ordinal);
        Assert.Contains("file not found", missing.Message, StringComparison.Ordinal);
        Assert.Contains("/dir", missing.Suggestion, StringComparison.Ordinal);
        var invalid = LoadFail("/main.scs", Provider(("/main.scs", "import \"lib.txt\" as lib\n")));
        Assert.Contains("lib.txt", invalid.Message, StringComparison.Ordinal);
        Assert.Contains(".scs", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cycle_through_the_entry_script_is_detected_in_the_last_file_of_the_chain()
    {
        var provider = Provider(("/main.scs", "import \"a.scs\" as a\n"), ("/a.scs", "import \"main.scs\" as m\n"));
        var error = LoadFail("/main.scs", provider);
        Assert.Equal((ErrorCodes.Module0002, "a.scs", 1, 8), (error.Code, Path.GetFileName(error.FilePath), error.Start.Line, error.Start.Column));
        Assert.Contains("main.scs", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_module_importing_itself_is_a_cycle()
    {
        var provider = Provider(("/main.scs", "import \"a.scs\" as a\n"), ("/a.scs", "import \"a.scs\" as again\n"));
        Assert.Equal(ErrorCodes.Module0002, LoadFail("/main.scs", provider).Code);
    }

    [Fact]
    public void The_cycle_message_lists_the_chain()
    {
        var provider = Provider(("/main.scs", "import \"a.scs\" as a\n"), ("/a.scs", "import \"b.scs\" as b\n"), ("/b.scs", "import \"a.scs\" as a\n"));
        var message = LoadFail("/main.scs", provider).Message;
        Assert.Contains("/a.scs → /b.scs → /a.scs", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_depth_of_exactly_thirty_two_is_accepted_and_thirty_three_is_not()
    {
        LoadOk("/work/main.scs", ChainProvider(32));
        var error = LoadFail("/work/main.scs", ChainProvider(33));
        Assert.Equal((ErrorCodes.Module0003, "m32.scs", 2, 8), (error.Code, Path.GetFileName(error.FilePath), error.Start.Line, error.Start.Column));
    }

    private static InMemorySourceProvider ChainProvider(int length)
    {
        var provider = new InMemorySourceProvider().AddText("/work/main.scs", "import \"m1.scs\" as chain\n");
        foreach (var (fileName, bytes) in ConformanceCases.GenerateImportChain(length))
        {
            provider.AddBytes("/work/" + fileName, bytes);
        }

        return provider;
    }

    [Fact]
    public void An_unreadable_entry_script_is_the_cli_error_without_position()
    {
        var result = ProgramLoader.Load("/missing.scs", new InMemorySourceProvider());
        var error = Assert.IsType<Diagnostic>(result.Error);
        Assert.Equal(ErrorCodes.Cli0009, error.Code);
        Assert.Null(error.File);
        Assert.Contains("/missing.scs", error.Message, StringComparison.Ordinal);
        Assert.Contains("file not found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entry_script_with_invalid_utf8_is_a_parse_error_not_a_cli_error()
    {
        var provider = new InMemorySourceProvider().AddBytes("/main.scs", [.. "let x = \"a"u8, (byte)0xFF, .. "\"\n"u8]);
        var error = LoadFail("/main.scs", provider);
        Assert.Equal((ErrorCodes.Parse0001, 1, 11), (error.Code, error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void A_module_with_invalid_utf8_is_a_parse_error_located_in_the_module()
    {
        var provider = new InMemorySourceProvider()
            .AddText("/main.scs", "import \"lib.scs\" as lib\n")
            .AddBytes("/lib.scs", [0xC0, 0x80]);
        var error = LoadFail("/main.scs", provider);
        Assert.Equal((ErrorCodes.Parse0001, "lib.scs", 1, 1), (error.Code, Path.GetFileName(error.FilePath), error.Start.Line, error.Start.Column));
    }

    [Fact]
    public void A_module_over_the_size_limit_is_a_parse_error()
    {
        var provider = new InMemorySourceProvider()
            .AddText("/main.scs", "import \"lib.scs\" as lib\n")
            .AddBytes("/lib.scs", new byte[Limits.MaximumSourceBytes + 1]);
        Assert.Equal(ErrorCodes.Parse0004, LoadFail("/main.scs", provider).Code);
    }

    [Fact]
    public void Duplicate_aliases_are_left_to_the_checker_and_the_first_one_wins_in_the_import_map()
    {
        var program = LoadOk(
            "/main.scs",
            Provider(("/main.scs", "import \"a.scs\" as same\nimport \"b.scs\" as same\n"), ("/a.scs", "public let v = 1\n"), ("/b.scs", "public let w = 2\n")));
        Assert.Equal(2, program.Modules.Count);
        Assert.Equal("/a.scs", program.Entry.Imports["same"].CanonicalPath);
        Assert.Equal(2, program.Entry.Program.Imports.Count);
    }

    [Fact]
    public void The_in_memory_provider_normalizes_paths()
    {
        var provider = new InMemorySourceProvider().AddText("/a/./b/../c.scs", "x");
        Assert.Equal("/a/c.scs", provider.Canonicalize("a//c.scs"));
        Assert.True(provider.TryReadBytes("/a/c.scs", out var bytes, out _));
        Assert.Equal("x"u8.ToArray(), bytes);
        Assert.False(provider.TryReadBytes("/a/missing.scs", out _, out var error));
        Assert.Equal("file not found", error);
    }

    [Fact]
    public void A_standard_input_entry_loads_alone_without_a_path()
    {
        var result = ProgramLoader.LoadStandardInput("io.write_standard_output_line(\"a\")\n"u8.ToArray(), new InMemorySourceProvider());

        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        var entry = result.Program!.Entry;
        Assert.False(entry.IsModule);
        Assert.Equal(ProgramLoader.StandardInputSourceName, entry.Source.Path);
        Assert.Equal("<stdin>", entry.Source.Path);
        Assert.Equal(string.Empty, entry.CanonicalPath);
        Assert.Empty(result.Program.Modules);
        Assert.Empty(entry.Imports);
    }

    [Fact]
    public void An_import_of_the_standard_input_entry_resolves_against_the_current_directory()
    {
        // The in-memory provider holds the files; the wrapper supplies the current directory `/work`, which the
        // entry (no path) has to use: `import "lib/a.scs"` resolves to `/work/lib/a.scs`.
        var files = new InMemorySourceProvider().AddText("/work/lib/a.scs", "public let one = 1\n");
        var provider = new CurrentDirectorySourceProvider(files, "/work");

        var result = ProgramLoader.LoadStandardInput("import \"lib/a.scs\" as lib\nio.write_standard_output_line(\"x\")\n"u8.ToArray(), provider);

        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        var module = Assert.Single(result.Program!.Modules);
        Assert.Equal("/work/lib/a.scs", module.CanonicalPath);
        Assert.Equal("lib/a.scs", module.Source.Path);
        Assert.Equal(module.CanonicalPath, provider.Canonicalize(module.Source.Path));
        Assert.True(module.IsModule);
        Assert.Same(module, result.Program.Entry.Imports["lib"]);
        Assert.Equal(string.Empty, result.Program.Entry.CanonicalPath);
    }

    [Fact]
    public void A_module_of_the_standard_input_entry_imports_relative_to_itself()
    {
        var files = new InMemorySourceProvider()
            .AddText("/work/lib/a.scs", "import \"b.scs\" as b\npublic let one = 1\n")
            .AddText("/work/lib/b.scs", "public let two = 2\n");
        var provider = new CurrentDirectorySourceProvider(files, "/work");

        var result = ProgramLoader.LoadStandardInput("import \"lib/a.scs\" as a\n"u8.ToArray(), provider);

        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        Assert.Equal(["/work/lib/b.scs", "/work/lib/a.scs"], result.Program!.Modules.Select(module => module.CanonicalPath));
        Assert.Same(result.Program.Modules[0], result.Program.Modules[1].Imports["b"]);
    }

    [Fact]
    public void A_standard_input_entry_with_invalid_utf8_is_a_parse_error_located_in_stdin()
    {
        var error = ProgramLoader.LoadStandardInput([.. "abc "u8, 0xFF], new InMemorySourceProvider()).Error;

        var diagnostic = Assert.IsType<Diagnostic>(error);
        Assert.Equal(ErrorCodes.Parse0001, diagnostic.Code);
        Assert.Equal("<stdin>", diagnostic.FilePath);
        Assert.Equal((1, 5), (diagnostic.Start.Line, diagnostic.Start.Column));
    }

    [Fact]
    public void Empty_standard_input_is_an_empty_program()
    {
        var result = ProgramLoader.LoadStandardInput([], new InMemorySourceProvider());

        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        Assert.Empty(result.Program!.Entry.Program.Statements);
        Assert.Empty(result.Program.Entry.Program.Imports);
        Assert.Empty(result.Program.Modules);
        Assert.Equal(string.Empty, result.Program.Entry.Source.Text);
        Assert.Equal(string.Empty, result.Program.Entry.CanonicalPath);
        Assert.Equal("<stdin>", result.Program.Entry.Source.Path);
    }
}

public sealed class FileSystemProviderTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "scripts-syntax-fs", Guid.NewGuid().ToString("N"));

    public FileSystemProviderTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void A_symbolic_link_to_a_module_has_the_identity_of_its_target()
    {
        File.WriteAllText(Path.Combine(directory, "real.scs"), "public let v = 1\n");
        File.CreateSymbolicLink(Path.Combine(directory, "link.scs"), "real.scs");
        File.WriteAllText(Path.Combine(directory, "main.scs"), "import \"real.scs\" as a\nimport \"link.scs\" as b\n");
        var result = ProgramLoader.Load(Path.Combine(directory, "main.scs"), new FileSystemSourceProvider());
        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        Assert.Single(result.Program!.Modules);
        Assert.Same(result.Program.Entry.Imports["a"], result.Program.Entry.Imports["b"]);
    }

    [Fact]
    public void A_symbolic_link_to_a_directory_is_resolved_in_the_middle_of_a_path()
    {
        Directory.CreateDirectory(Path.Combine(directory, "target"));
        File.CreateSymbolicLink(Path.Combine(directory, "alias"), "target");
        File.WriteAllText(Path.Combine(directory, "target", "lib.scs"), "public let v = 1\n");
        var provider = new FileSystemSourceProvider();
        var viaLink = provider.Canonicalize(Path.Combine(directory, "alias", "lib.scs"));
        var direct = provider.Canonicalize(Path.Combine(directory, "target", "lib.scs"));
        Assert.Equal(direct, viaLink);
        Assert.EndsWith("/target/lib.scs", direct, StringComparison.Ordinal);
    }

    [Fact]
    public void Parent_segments_are_resolved_after_following_links()
    {
        Directory.CreateDirectory(Path.Combine(directory, "deep", "inner"));
        File.CreateSymbolicLink(Path.Combine(directory, "shortcut"), Path.Combine("deep", "inner"));
        var provider = new FileSystemSourceProvider();
        Assert.Equal(
            provider.Canonicalize(Path.Combine(directory, "deep", "x.scs")),
            provider.Canonicalize(Path.Combine(directory, "shortcut", "..", "x.scs")));
    }

    [Fact]
    public void A_symbolic_link_loop_does_not_hang_the_canonicalization()
    {
        File.CreateSymbolicLink(Path.Combine(directory, "loop"), "loop");
        var canonical = new FileSystemSourceProvider().Canonicalize(Path.Combine(directory, "loop"));
        Assert.EndsWith("/loop", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_reports_clear_reasons()
    {
        var provider = new FileSystemSourceProvider();
        Assert.False(provider.TryReadBytes(Path.Combine(directory, "missing.scs"), out _, out var missing));
        Assert.Equal("file not found", missing);
        Assert.False(provider.TryReadBytes(directory, out _, out var isDirectory));
        Assert.Contains("directory", isDirectory, StringComparison.Ordinal);
        Assert.True(provider.TryReadBytes("/dev/zero", out var deviceBytes, out _));
        Assert.Equal(Limits.MaximumSourceBytes + 1, deviceBytes.Length);
        Assert.Equal(ErrorCodes.Parse0004, ProgramLoader.Load("/dev/zero", provider).Error!.Code);
        File.WriteAllBytes(Path.Combine(directory, "ok.scs"), "abc"u8.ToArray());
        Assert.True(provider.TryReadBytes(Path.Combine(directory, "ok.scs"), out var bytes, out _));
        Assert.Equal("abc"u8.ToArray(), bytes);
    }

    [Fact]
    public void A_missing_entry_script_is_cli_0009_and_a_directory_too()
    {
        var provider = new FileSystemSourceProvider();
        Assert.Equal(ErrorCodes.Cli0009, ProgramLoader.Load(Path.Combine(directory, "absent.scs"), provider).Error!.Code);
        Assert.Equal(ErrorCodes.Cli0009, ProgramLoader.Load(directory, provider).Error!.Code);
    }

    [Fact]
    public void A_relative_entry_path_uses_the_current_directory_for_canonicalization()
    {
        var provider = new FileSystemSourceProvider();
        var canonical = provider.Canonicalize("some/relative.scs");
        Assert.Equal(Path.Combine(Directory.GetCurrentDirectory(), "some", "relative.scs"), canonical);
    }

    [Fact]
    public void A_module_file_written_with_a_bom_is_read_without_it()
    {
        File.WriteAllBytes(Path.Combine(directory, "lib.scs"), [.. new byte[] { 0xEF, 0xBB, 0xBF }, .. "public let v = 1\n"u8]);
        File.WriteAllText(Path.Combine(directory, "main.scs"), "import \"lib.scs\" as lib\n", new UTF8Encoding(false));
        var result = ProgramLoader.Load(Path.Combine(directory, "main.scs"), new FileSystemSourceProvider());
        Assert.True(result.Error is null, Parsing.Describe(result.Error));
        Assert.StartsWith("public", result.Program!.Modules.Single().Source.Text, StringComparison.Ordinal);
    }
}
