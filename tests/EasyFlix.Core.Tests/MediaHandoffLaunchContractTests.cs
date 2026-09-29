using EasyFlix.Core.Services;

namespace EasyFlix.Core.Tests;

public sealed class MediaHandoffLaunchContractTests
{
    [Fact]
    public void InvalidFlagInvocation_IsStillRecognizedForCliValidation()
    {
        Assert.True(MediaHandoffLaunchContract.IsImportInvocation(["--import-handoff"]));
        Assert.False(MediaHandoffLaunchContract.TryGetImportPath(["--import-handoff"], out _));
    }

    [Fact]
    public void FlaggedTxtImport_RemainsSupported()
    {
        Assert.True(MediaHandoffLaunchContract.TryGetImportPath(["--import-handoff", @"C:\Temp\batch.txt"], out var path));
        Assert.Equal(@"C:\Temp\batch.txt", path);
    }

    [Fact]
    public void DirectDedicatedHandoff_IsSupportedButDirectTxtIsNot()
    {
        Assert.True(MediaHandoffLaunchContract.IsImportInvocation([@"C:\Temp\batch.EASYFLIXHANDOFF"]));
        Assert.True(MediaHandoffLaunchContract.TryGetImportPath([@"C:\Temp\batch.easyflixhandoff"], out var path));
        Assert.Equal(@"C:\Temp\batch.easyflixhandoff", path);
        Assert.False(MediaHandoffLaunchContract.IsImportInvocation([@"C:\Temp\batch.txt"]));
        Assert.False(MediaHandoffLaunchContract.TryGetImportPath([@"C:\Temp\batch.txt"], out _));
    }

    [Fact]
    public void OpenCommand_IsQuotedAndUsesNativeImportFlag()
    {
        var quote = ((char)34).ToString();
        var command = MediaHandoffLaunchContract.BuildOpenCommand(@"C:\Program Files\EasyFlix\EasyFlix.exe");
        var expected = quote
            + @"C:\Program Files\EasyFlix\EasyFlix.exe"
            + quote
            + " --import-handoff "
            + quote
            + "%1"
            + quote;

        Assert.Equal(expected, command);
    }

    [Fact]
    public void DedicatedExtension_IsNeverGenericTxt()
    {
        Assert.Equal(".easyflixhandoff", MediaHandoffLaunchContract.DedicatedExtension);
        Assert.NotEqual(".txt", MediaHandoffLaunchContract.DedicatedExtension);
        Assert.False(MediaHandoffLaunchContract.IsDedicatedHandoffPath("notes.txt"));
    }
}