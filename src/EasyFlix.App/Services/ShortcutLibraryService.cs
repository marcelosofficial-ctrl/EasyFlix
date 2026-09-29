using System.Runtime.InteropServices;
using System.Text.Json;
using System.IO;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record ApplySummary(int Created, int Updated, int Skipped, string LogPath);

public sealed class ShortcutLibraryService
{
    public ApplySummary Apply(IEnumerable<LibraryAction> plan, string libraryPath, CancellationToken cancellationToken = default)
    {
        var actions = plan.ToArray();
        var logRows = new List<object>();
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new PlatformNotSupportedException("Windows Script Host is unavailable.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            foreach (var action in actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(action.Item.SourcePath) && !Directory.Exists(action.Item.SourcePath))
                {
                    logRows.Add(new { action.Item.SourcePath, action.ShortcutPath, Status = "MissingTarget" });
                    skipped++;
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(action.ShortcutPath)!);
                dynamic shortcut = shell.CreateShortcut(action.ShortcutPath);
                try
                {
                    var existed = File.Exists(action.ShortcutPath);
                    var workingDirectory = action.Item.IsFolder
                        ? action.Item.SourcePath
                        : Path.GetDirectoryName(action.Item.SourcePath)!;
                    var description = $"EasyFlix: {action.Item.SuggestedName}";
                    var desiredIcon = !string.IsNullOrWhiteSpace(action.Item.ArtworkPath) && File.Exists(action.Item.ArtworkPath)
                        ? $"{action.Item.ArtworkPath},0"
                        : null;
                    var needsRefresh = !existed ||
                        !SamePath(Convert.ToString(shortcut.TargetPath), action.Item.SourcePath) ||
                        !SamePath(Convert.ToString(shortcut.WorkingDirectory), workingDirectory) ||
                        !string.Equals(Convert.ToString(shortcut.Description), description, StringComparison.Ordinal) ||
                        desiredIcon is not null && !string.Equals(Convert.ToString(shortcut.IconLocation), desiredIcon, StringComparison.OrdinalIgnoreCase);
                    if (!needsRefresh)
                    {
                        logRows.Add(new { action.Item.SourcePath, action.ShortcutPath, Status = "Current" });
                        skipped++;
                        continue;
                    }
                    shortcut.TargetPath = action.Item.SourcePath;
                    shortcut.WorkingDirectory = workingDirectory;
                    shortcut.Description = description;
                    if (desiredIcon is not null) shortcut.IconLocation = desiredIcon;
                    shortcut.Save();
                    if (existed) updated++; else created++;
                    logRows.Add(new { action.Item.SourcePath, action.ShortcutPath, Status = existed ? "Updated" : "Created" });
                }
                finally
                {
                    if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        var logFolder = Path.Combine(libraryPath, ".reelshelf", "logs");
        Directory.CreateDirectory(logFolder);
        var logPath = Path.Combine(logFolder, $"apply-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(logPath, JsonSerializer.Serialize(logRows, new JsonSerializerOptions { WriteIndented = true }));
        return new ApplySummary(created, updated, skipped, logPath);
    }

    private static bool SamePath(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
