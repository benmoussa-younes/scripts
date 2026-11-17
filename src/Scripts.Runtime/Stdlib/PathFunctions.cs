using Scripts.Runtime.Platform;
using static Scripts.Runtime.ArgumentAccess;

namespace Scripts.Runtime;

/// <summary>base_name, extension, parent, to_string, is_absolute, normalized, file_stem, with_extension and matches_glob (SPEC-STDLIB 7.5.1).</summary>
internal static class PathFunctions
{
    public static void Register(Dictionary<string, StdlibImplementation> table)
    {
        table["path.base_name"] = (context, arguments) => new StringValue(LexicalPath.BaseName(PathTextOf(arguments, 0)));
        table["path.extension"] = (context, arguments) => new StringValue(LexicalPath.Extension(PathTextOf(arguments, 0)));
        table["path.parent"] = (context, arguments) => new PathValue(LexicalPath.Parent(PathTextOf(arguments, 0)));
        table["path.to_string"] = (context, arguments) => new StringValue(PathTextOf(arguments, 0));
        table["path.is_absolute"] = (context, arguments) => BooleanValue.From(PathTextOf(arguments, 0).StartsWith('/'));
        table["path.normalized"] = (context, arguments) => new PathValue(LexicalPath.Normalize(PathTextOf(arguments, 0)));
        table["path.file_stem"] = (context, arguments) => new StringValue(LexicalPath.FileStem(PathTextOf(arguments, 0)));
        table["path.with_extension"] = (context, arguments) => new PathValue(LexicalPath.WithExtension(PathTextOf(arguments, 0), Text(arguments, 1)));
        table["path.matches_glob"] = MatchesGlob;
    }

    private static Value MatchesGlob(RuntimeContext context, Value[] arguments)
    {
        var patternText = Text(arguments, 1);
        if (!GlobPattern.TryCompile(patternText, out var pattern, out var reason))
        {
            return ResultValue.Error(ErrorValues.Error("invalid_argument", patternText, reason));
        }

        return ResultValue.Ok(BooleanValue.From(pattern!.MatchesPath(PathTextOf(arguments, 0))));
    }
}
