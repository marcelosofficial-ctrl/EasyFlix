using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaImportCommandContractTests
{
    [Fact]
    public void ExitCodes_AreStable()
    {
        Assert.Equal(0, MediaImportCommandContract.GetExitCode(Result(MediaImportFailureKind.None)));
        Assert.Equal(2, MediaImportCommandContract.GetExitCode(Result(MediaImportFailureKind.Validation)));
        Assert.Equal(3, MediaImportCommandContract.GetExitCode(Result(MediaImportFailureKind.Import)));
        Assert.Equal(4, MediaImportCommandContract.GetExitCode(Result(MediaImportFailureKind.Verification)));
        Assert.Equal(5, MediaImportCommandContract.GetExitCode(Result(MediaImportFailureKind.Internal)));
    }

    [Fact]
    public void UnresolvedItems_CannotReportSuccess()
    {
        var result = Result(MediaImportFailureKind.None) with { Unresolved = 1 };

        Assert.False(result.Success);
        Assert.Equal(4, MediaImportCommandContract.GetExitCode(result));
    }

    [Fact]
    public void FinalBlock_IsMachineParseable()
    {
        var result = new MediaImportCommandResult(
            MediaImportFailureKind.None,
            "complete",
            Imported: 3,
            Updated: 2,
            Unresolved: 0,
            ReportPath: @"C:\Temp\EasyFlix Report.txt");

        var lines = MediaImportCommandContract.FormatFinalBlock(result)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("RESULT=PASS", lines[0]);
        Assert.Equal("STAGE=COMPLETE", lines[1]);
        Assert.Contains("IMPORTED=3", lines);
        Assert.Contains("UPDATED=2", lines);
        Assert.Contains("UNRESOLVED=0", lines);
        Assert.Contains(@"REPORT=C:\Temp\EasyFlix Report.txt", lines);
        Assert.Contains("EXIT_CODE=0", lines);
    }

    private static MediaImportCommandResult Result(MediaImportFailureKind kind)
        => new(kind, kind == MediaImportFailureKind.None ? "COMPLETE" : kind.ToString(), 0, 0, 0, null);
}