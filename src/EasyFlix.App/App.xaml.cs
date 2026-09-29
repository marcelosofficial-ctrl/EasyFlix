using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using EasyFlix.App.Services;
using EasyFlix.Core.Services;

namespace EasyFlix.App;

public partial class App : Application
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    private async void App_Startup(object sender, StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (MediaHandoffLaunchContract.IsImportInvocation(e.Args))
        {
            PrepareCliOutput();
            var result = await RunImportHandoffCliAsync(e.Args);
            var exitCode = MediaImportCommandContract.GetExitCode(result);
            Console.Write(MediaImportCommandContract.FormatFinalBlock(result));
            Environment.ExitCode = exitCode;
            Shutdown(exitCode);
            return;
        }

        LegacyTextPolish.Enable();
        WindowChromeService.Enable();

        var settings = new AppSettingsService().Load();
        if (string.IsNullOrWhiteSpace(settings.SourceRoots))
            new FirstRunWindow().ShowDialog();

        var browse = new BrowseWindow();
        MainWindow = browse;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        browse.Show();
    }

    private static async Task<MediaImportCommandResult> RunImportHandoffCliAsync(string[] args)
    {
        if (!MediaHandoffLaunchContract.TryGetImportPath(args, out var sourcePath))
        {
            return new MediaImportCommandResult(
                MediaImportFailureKind.Validation,
                "ARGUMENTS",
                0,
                0,
                0,
                null);
        }

        try
        {
            var store = new CatalogStore(CatalogLocation.DatabasePath);
            var execution = new MediaIntakeExecutionService(store);
            return (await execution.ExecuteAsync(sourcePath!)).Command;
        }
        catch
        {
            return new MediaImportCommandResult(
                MediaImportFailureKind.Internal,
                "INTERNAL",
                0,
                0,
                0,
                null);
        }
    }

    private static void PrepareCliOutput()
    {
        try
        {
            if (!Console.IsOutputRedirected)
                _ = AttachConsole(AttachParentProcess);

            var output = Console.OpenStandardOutput();
            if (output != Stream.Null)
                Console.SetOut(new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true });

            var error = Console.OpenStandardError();
            if (error != Stream.Null)
                Console.SetError(new StreamWriter(error, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch
        {
            // The final execution report remains the durable fallback for automation.
        }
    }
}