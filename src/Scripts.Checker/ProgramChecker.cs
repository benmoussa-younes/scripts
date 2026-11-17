using System.Runtime.ExceptionServices;
using Scripts.Core;

namespace Scripts.Checker;

/// <summary>Semantic analysis of a loaded program (DESIGN 8.1): all errors of all files, ordered, plus lint warnings.</summary>
public static class ProgramChecker
{
    private const int AnalysisStackBytes = 64 * 1024 * 1024;

    public static CheckResult Check(LoadedProgram program)
    {
        CheckResult? result = null;
        Exception? failure = null;
        var analysisThread = new Thread(
            () =>
            {
                try
                {
                    result = Analyze(program);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            },
            AnalysisStackBytes);
        analysisThread.Start();
        analysisThread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    private static CheckResult Analyze(LoadedProgram program)
    {
        ConstantAnalysis.MarkConstants(program);
        var sink = new DiagnosticSink();
        var moduleInfos = new Dictionary<LoadedFile, ModuleInfo>();
        var fileOrder = new Dictionary<SourceFile, int>();
        foreach (var file in program.AllFilesInLoadOrder())
        {
            fileOrder[file.Source] = fileOrder.Count;
            moduleInfos[file] = new FileAnalyzer(file, sink, moduleInfos).Analyze();
        }

        int OrderOf(SourceFile? sourceFile) => sourceFile is not null && fileOrder.TryGetValue(sourceFile, out var order) ? order : -1;
        var errors = sink.Errors.ToList();
        var warnings = sink.Warnings.ToList();
        errors.Sort((left, right) => Diagnostics.Compare(left, right, OrderOf));
        warnings.Sort((left, right) => Diagnostics.Compare(left, right, OrderOf));
        return new CheckResult(errors, warnings);
    }
}
