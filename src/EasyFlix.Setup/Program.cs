using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    private const string ProductName = "EasyFlix";
    private const string ProductVersion = "1.3.0";
    private const string Publisher = "EasyFlix contributors";
    private const string PayloadResourceName = "EasyFlix.Payload.exe";
    private const string HandoffProgId = "EasyFlix.MediaHandoff";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\EasyFlix";

    private enum ExitCode
    {
        Success = 0,
        Cancelled = 1,
        InvalidArguments = 2,
        EasyFlixRunning = 3,
        PayloadUnavailable = 4,
        OperationFailed = 5
    }

    private sealed class Options
    {
        public bool Silent { get; set; }
        public bool Uninstall { get; set; }
        public bool UninstallWorker { get; set; }
        public bool TestMode { get; set; }
        public string? InstallRoot { get; set; }
        public int? ParentPid { get; set; }
        public bool InstallRootWasSpecified { get; set; }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (!TryParseArgs(args, out var options))
        {
            return (int)ExitCode.InvalidArguments;
        }

        try
        {
            if (options.UninstallWorker)
            {
                return RunUninstallWorker(options);
            }

            if (options.Uninstall)
            {
                return RunUninstall(options);
            }

            return RunInstall(options);
        }
        catch (Exception ex)
        {
            ShowError(options.Silent || options.TestMode, $"EasyFlix setup failed.\n\n{ex.Message}");
            return (int)ExitCode.OperationFailed;
        }
    }

    private static bool TryParseArgs(string[] args, out Options options)
    {
        options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--silent":
                    options.Silent = true;
                    break;

                case "--uninstall":
                    options.Uninstall = true;
                    break;

                case "--uninstall-worker":
                    options.UninstallWorker = true;
                    break;

                case "--test-mode":
                    options.TestMode = true;
                    break;

                case "--install-root":
                    if (i + 1 >= args.Length)
                    {
                        return false;
                    }

                    options.InstallRoot = args[++i];
                    options.InstallRootWasSpecified = true;
                    break;

                case "--parent-pid":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parentPid) || parentPid <= 0)
                    {
                        return false;
                    }

                    options.ParentPid = parentPid;
                    break;

                default:
                    return false;
            }
        }

        if (options.Uninstall && options.UninstallWorker)
        {
            return false;
        }

        if (options.UninstallWorker && !options.ParentPid.HasValue)
        {
            return false;
        }

        if (options.TestMode && !options.InstallRootWasSpecified)
        {
            return false;
        }

        var root = options.InstallRoot;
        if (!string.IsNullOrWhiteSpace(root) && !Path.IsPathFullyQualified(root))
        {
            return false;
        }

        return true;
    }

    private static string GetInstallRoot(Options options)
    {
        if (!string.IsNullOrWhiteSpace(options.InstallRoot))
        {
            return Path.GetFullPath(options.InstallRoot);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            ProductName);
    }

    private static int RunInstall(Options options)
    {
        if (IsEasyFlixRunning())
        {
            ShowError(options.Silent || options.TestMode, "EasyFlix is currently running. Close EasyFlix and run the installer again.");
            return (int)ExitCode.EasyFlixRunning;
        }

        var installRoot = GetInstallRoot(options);
        Directory.CreateDirectory(installRoot);

        var installedExe = Path.Combine(installRoot, "EasyFlix.exe");
        var installedUninstaller = Path.Combine(installRoot, "EasyFlix.Uninstall.exe");

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName);
        if (payload is null)
        {
            ShowError(options.Silent || options.TestMode, "This setup file does not contain the EasyFlix application payload.");
            return (int)ExitCode.PayloadUnavailable;
        }

        WriteFileAtomically(payload, installedExe);

        var setupPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
        {
            throw new InvalidOperationException("Could not locate the running setup executable.");
        }

        WriteFileAtomically(setupPath, installedUninstaller);

        if (!options.TestMode)
        {
            CreateShortcuts(installedExe);
            WriteUninstallRegistry(installRoot, installedExe, installedUninstaller);
        }

        if (!options.Silent && !options.TestMode)
        {
            var result = MessageBox.Show(
                "EasyFlix was installed successfully.\n\nLaunch EasyFlix now?",
                "EasyFlix Setup",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    WorkingDirectory = installRoot,
                    UseShellExecute = true
                });
            }
        }

        return (int)ExitCode.Success;
    }

    private static int RunUninstall(Options options)
    {
        if (IsEasyFlixRunning())
        {
            ShowError(options.Silent || options.TestMode, "EasyFlix is currently running. Close EasyFlix before uninstalling.");
            return (int)ExitCode.EasyFlixRunning;
        }

        if (!options.Silent && !options.TestMode)
        {
            var answer = MessageBox.Show(
                "Uninstall EasyFlix?\n\nYour media files and EasyFlix catalog/settings under LocalAppData will be preserved.",
                "Uninstall EasyFlix",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (answer != DialogResult.Yes)
            {
                return (int)ExitCode.Cancelled;
            }
        }

        var installRoot = GetInstallRoot(options);
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
        {
            throw new InvalidOperationException("Could not locate the running uninstaller.");
        }

        var workerPath = Path.Combine(
            Path.GetTempPath(),
            $"EasyFlix.Uninstall.{Guid.NewGuid():N}.exe");

        File.Copy(currentPath, workerPath, overwrite: false);

        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("--uninstall-worker");
        startInfo.ArgumentList.Add("--install-root");
        startInfo.ArgumentList.Add(installRoot);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        if (options.Silent)
        {
            startInfo.ArgumentList.Add("--silent");
        }

        if (options.TestMode)
        {
            startInfo.ArgumentList.Add("--test-mode");
        }

        var worker = Process.Start(startInfo);
        if (worker is null)
        {
            throw new InvalidOperationException("Could not start the EasyFlix uninstall worker.");
        }

        worker.Dispose();
        return (int)ExitCode.Success;
    }

    private static int RunUninstallWorker(Options options)
    {
        var workerPath = Environment.ProcessPath;

        try
        {
            if (options.ParentPid.HasValue)
            {
                try
                {
                    using var parent = Process.GetProcessById(options.ParentPid.Value);
                    parent.WaitForExit(30000);
                }
                catch (ArgumentException)
                {
                }
            }

            if (IsEasyFlixRunning())
            {
                return (int)ExitCode.EasyFlixRunning;
            }

            var installRoot = GetInstallRoot(options);
            var installedExe = Path.Combine(installRoot, "EasyFlix.exe");

            if (!options.TestMode)
            {
                RemoveShortcuts();
                RemoveUninstallRegistry();
                RemoveOwnedHandoffAssociation(installedExe);
            }

            if (Directory.Exists(installRoot))
            {
                Directory.Delete(installRoot, recursive: true);
            }

            return (int)ExitCode.Success;
        }
        catch
        {
            return (int)ExitCode.OperationFailed;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(workerPath))
            {
                ScheduleSelfDelete(workerPath);
            }
        }
    }

    private static bool IsEasyFlixRunning()
    {
        foreach (var process in Process.GetProcessesByName(ProductName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void WriteFileAtomically(Stream source, string destination)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination directory is unavailable.");

        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.WriteThrough))
            {
                source.CopyTo(file);
                file.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void WriteFileAtomically(string sourcePath, string destination)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        WriteFileAtomically(source, destination);
    }

    private static void CreateShortcuts(string installedExe)
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "EasyFlix.lnk");

        var startMenuFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "EasyFlix");

        Directory.CreateDirectory(startMenuFolder);

        CreateShortcut(desktopShortcut, installedExe);
        CreateShortcut(Path.Combine(startMenuFolder, "EasyFlix.lnk"), installedExe);
    }

    private static void CreateShortcut(string shortcutPath, string targetPath)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("Windows Script Host is unavailable.");

        object? shell = null;
        object? shortcut = null;

        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not create Windows Script Host shell.");

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath });

            if (shortcut is null)
            {
                throw new InvalidOperationException("Could not create shortcut.");
            }

            var shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetPath });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(targetPath)! });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { $"{targetPath},0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "EasyFlix" });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RemoveShortcuts()
    {
        var desktopShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "EasyFlix.lnk");

        var startMenuFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "EasyFlix");

        if (File.Exists(desktopShortcut))
        {
            File.Delete(desktopShortcut);
        }

        if (Directory.Exists(startMenuFolder))
        {
            Directory.Delete(startMenuFolder, recursive: true);
        }
    }

    private static void WriteUninstallRegistry(string installRoot, string installedExe, string installedUninstaller)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Could not create the EasyFlix uninstall registry entry.");

        key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", ProductVersion, RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", $"\"{installedExe}\",0", RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{installedUninstaller}\" --uninstall", RegistryValueKind.String);
        key.SetValue("QuietUninstallString", $"\"{installedUninstaller}\" --uninstall --silent", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);

        var estimatedBytes = Directory
            .EnumerateFiles(installRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum();

        var estimatedKb = Math.Max(1L, (estimatedBytes + 1023L) / 1024L);
        key.SetValue("EstimatedSize", checked((int)Math.Min(int.MaxValue, estimatedKb)), RegistryValueKind.DWord);
    }

    private static void RemoveUninstallRegistry()
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
    }

    private static void RemoveOwnedHandoffAssociation(string installedExe)
    {
        const string extensionKeyPath = @"Software\Classes\.easyflixhandoff";
        const string commandKeyPath = @"Software\Classes\EasyFlix.MediaHandoff\shell\open\command";
        const string progIdKeyPath = @"Software\Classes\EasyFlix.MediaHandoff";

        using var extensionKey = Registry.CurrentUser.OpenSubKey(extensionKeyPath);
        var owner = extensionKey?.GetValue(null) as string;
        if (!string.Equals(owner, HandoffProgId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var commandKey = Registry.CurrentUser.OpenSubKey(commandKeyPath);
        var command = commandKey?.GetValue(null) as string;
        if (!OpenCommandTargetsExactExecutable(command, installedExe))
        {
            return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(extensionKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(progIdKeyPath, throwOnMissingSubKey: false);
    }

    private static bool OpenCommandTargetsExactExecutable(string? command, string installedExe)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim();
        string executable;

        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote <= 1)
            {
                return false;
            }

            executable = trimmed.Substring(1, endQuote - 1);
        }
        else
        {
            var separator = trimmed.IndexOf(' ');
            executable = separator < 0 ? trimmed : trimmed.Substring(0, separator);
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(executable),
                Path.GetFullPath(installedExe),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void ScheduleSelfDelete(string workerPath)
    {
        try
        {
            var quotedPath = workerPath.Replace("\"", "\"\"");
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"ping 127.0.0.1 -n 2 >nul & del /f /q \"\"{quotedPath}\"\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch
        {
        }
    }

    private static void ShowError(bool suppressUi, string message)
    {
        if (!suppressUi)
        {
            MessageBox.Show(
                message,
                "EasyFlix Setup",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
