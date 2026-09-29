namespace EasyFlix.Core.Services;

public static class MediaHandoffLaunchContract
{
    public const string ImportFlag = "--import-handoff";
    public const string DedicatedExtension = ".easyflixhandoff";
    public const string ProgId = "EasyFlix.MediaHandoff";

    public static bool IsImportInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(arg => string.Equals(arg, ImportFlag, StringComparison.OrdinalIgnoreCase))
            || (args.Count == 1 && IsDedicatedHandoffPath(args[0]));
    }

    public static bool TryGetImportPath(IReadOnlyList<string> args, out string? sourcePath)
    {
        ArgumentNullException.ThrowIfNull(args);
        sourcePath = null;

        if (args.Count == 2
            && string.Equals(args[0], ImportFlag, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(args[1]))
        {
            sourcePath = args[1];
            return true;
        }

        if (args.Count == 1 && IsDedicatedHandoffPath(args[0]))
        {
            sourcePath = args[0];
            return true;
        }

        return false;
    }

    public static bool IsDedicatedHandoffPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && string.Equals(Path.GetExtension(path), DedicatedExtension, StringComparison.OrdinalIgnoreCase);

    public static string BuildOpenCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var quote = ((char)34).ToString();
        return quote + executablePath + quote + " " + ImportFlag + " " + quote + "%1" + quote;
    }

    public static string BuildIconValue(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        var quote = ((char)34).ToString();
        return quote + executablePath + quote + ",0";
    }
}