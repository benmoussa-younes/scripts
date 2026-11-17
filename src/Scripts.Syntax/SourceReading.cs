using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using Scripts.Core;

namespace Scripts.Syntax;

public sealed record SourceReadResult(SourceFile? Source, Diagnostic? Error);

/// <summary>Turns raw file bytes into a <see cref="SourceFile"/>: size limit, strict UTF-8 (BOM skipped), no NUL.</summary>
public static class SourceReading
{
    public static SourceReadResult Read(string path, byte[] bytes)
    {
        if (bytes.Length > Limits.MaximumSourceBytes)
        {
            var size = bytes.Length.ToString(CultureInfo.InvariantCulture);
            return Failure(Diagnostics.Create(ErrorCodes.Parse0004, new SourceFile(path, string.Empty), new Span(0, 0), ("size", size)));
        }

        var hasByteOrderMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var content = bytes.AsSpan(hasByteOrderMark ? 3 : 0);
        var characters = new char[content.Length];
        var status = Utf8.ToUtf16(content, characters, out var bytesRead, out var charactersWritten, replaceInvalidSequences: false, isFinalBlock: true);
        var validText = new string(characters, 0, charactersWritten);
        var nulIndex = validText.IndexOf('\0', StringComparison.Ordinal);
        if (nulIndex >= 0)
        {
            return Failure(Diagnostics.Create(ErrorCodes.Parse0002, new SourceFile(path, validText), new Span(nulIndex, nulIndex + 1)));
        }

        if (status == OperationStatus.Done)
        {
            return new SourceReadResult(new SourceFile(path, validText), null);
        }

        var lenientText = Encoding.UTF8.GetString(content);
        var invalidByte = content[bytesRead].ToString("X2", CultureInfo.InvariantCulture);
        return Failure(Diagnostics.Create(
            ErrorCodes.Parse0001,
            new SourceFile(path, lenientText),
            new Span(charactersWritten, charactersWritten + 1),
            ("byte", invalidByte)));
    }

    private static SourceReadResult Failure(Diagnostic diagnostic) => new(null, diagnostic);
}
