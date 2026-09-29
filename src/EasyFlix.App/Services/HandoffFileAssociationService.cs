using System.Runtime.InteropServices;
using EasyFlix.Core.Services;
using Microsoft.Win32;

namespace EasyFlix.App.Services;

public sealed class HandoffFileAssociationService
{
    private const string ClassesRoot = @"Software\Classes";
    private const uint AssociationChanged = 0x08000000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    public bool IsRegistered(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        using var extensionKey = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesRoot}\{MediaHandoffLaunchContract.DedicatedExtension}");
        var progId = extensionKey?.GetValue(null) as string;
        if (!string.Equals(progId, MediaHandoffLaunchContract.ProgId, StringComparison.Ordinal))
            return false;

        using var commandKey = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesRoot}\{MediaHandoffLaunchContract.ProgId}\shell\open\command");
        var command = commandKey?.GetValue(null) as string;
        return string.Equals(
            command,
            MediaHandoffLaunchContract.BuildOpenCommand(executablePath),
            StringComparison.Ordinal);
    }

    public void Register(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        using (var extensionKey = Registry.CurrentUser.CreateSubKey(
                   $@"{ClassesRoot}\{MediaHandoffLaunchContract.DedicatedExtension}",
                   writable: true))
        {
            extensionKey.SetValue(null, MediaHandoffLaunchContract.ProgId, RegistryValueKind.String);
            using var openWith = extensionKey.CreateSubKey("OpenWithProgids", writable: true);
            openWith.SetValue(MediaHandoffLaunchContract.ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(
                   $@"{ClassesRoot}\{MediaHandoffLaunchContract.ProgId}",
                   writable: true))
        {
            progIdKey.SetValue(null, "EasyFlix Media Handoff", RegistryValueKind.String);
            using var iconKey = progIdKey.CreateSubKey("DefaultIcon", writable: true);
            iconKey.SetValue(null, MediaHandoffLaunchContract.BuildIconValue(executablePath), RegistryValueKind.String);
            using var commandKey = progIdKey.CreateSubKey(@"shell\open\command", writable: true);
            commandKey.SetValue(null, MediaHandoffLaunchContract.BuildOpenCommand(executablePath), RegistryValueKind.String);
        }

        NotifyAssociationChanged();

        if (!IsRegistered(executablePath))
        {
            throw new InvalidOperationException(
                "Windows did not retain the .easyflixhandoff association. No association was enabled.");
        }
    }

    public void Unregister()
    {
        using var classesKey = Registry.CurrentUser.OpenSubKey(ClassesRoot, writable: true);
        if (classesKey is null)
            return;

        using (var extensionKey = classesKey.OpenSubKey(
                   MediaHandoffLaunchContract.DedicatedExtension,
                   writable: true))
        {
            var progId = extensionKey?.GetValue(null) as string;
            if (string.Equals(progId, MediaHandoffLaunchContract.ProgId, StringComparison.Ordinal))
                classesKey.DeleteSubKeyTree(MediaHandoffLaunchContract.DedicatedExtension, throwOnMissingSubKey: false);
        }

        classesKey.DeleteSubKeyTree(MediaHandoffLaunchContract.ProgId, throwOnMissingSubKey: false);
        NotifyAssociationChanged();
    }

    private static void NotifyAssociationChanged()
        => SHChangeNotify(AssociationChanged, 0, IntPtr.Zero, IntPtr.Zero);
}