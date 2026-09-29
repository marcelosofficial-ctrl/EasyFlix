namespace EasyFlix.Core.Services;

public enum MediaImportFailureKind
{
    None,
    Validation,
    Import,
    Verification,
    Internal
}

public sealed record MediaImportCommandResult(
    MediaImportFailureKind FailureKind,
    string Stage,
    int Imported,
    int Updated,
    int Unresolved,
    string? ReportPath)
{
    public bool Success => FailureKind == MediaImportFailureKind.None && Unresolved == 0;
}

public static class MediaImportCommandContract
{
    public const int SuccessExitCode = 0;
    public const int ValidationExitCode = 2;
    public const int ImportExitCode = 3;
    public const int VerificationExitCode = 4;
    public const int InternalExitCode = 5;

    public static int GetExitCode(MediaImportCommandResult result)
    {
        if (result.Success) return SuccessExitCode;
        if (result.Unresolved > 0 || result.FailureKind == MediaImportFailureKind.Verification)
            return VerificationExitCode;

        return result.FailureKind switch
        {
            MediaImportFailureKind.Validation => ValidationExitCode,
            MediaImportFailureKind.Import => ImportExitCode,
            MediaImportFailureKind.Internal => InternalExitCode,
            _ => InternalExitCode
        };
    }

    public static string FormatFinalBlock(MediaImportCommandResult result)
    {
        var exitCode = GetExitCode(result);
        var report = string.IsNullOrWhiteSpace(result.ReportPath) ? "<none>" : Clean(result.ReportPath);
        var stage = string.IsNullOrWhiteSpace(result.Stage) ? "INTERNAL" : Clean(result.Stage).ToUpperInvariant();

        return string.Join(
            Environment.NewLine,
            $"RESULT={(result.Success ? "PASS" : "FAIL")}",
            $"STAGE={stage}",
            $"IMPORTED={Math.Max(0, result.Imported)}",
            $"UPDATED={Math.Max(0, result.Updated)}",
            $"UNRESOLVED={Math.Max(0, result.Unresolved)}",
            $"REPORT={report}",
            $"EXIT_CODE={exitCode}") + Environment.NewLine;
    }

    private static string Clean(string value)
        => value.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
}