using System.IO;
using System.Text;
using EasyFlix.Core.Services;

namespace EasyFlix.App.Services;

public sealed record MediaIntakeExecutionOutcome(
    MediaImportCommandResult Command,
    string Message,
    MediaIntakePreview? Preview,
    string? BackupDirectory,
    MediaIntakeImportOutcome? ImportOutcome,
    MediaIntakeManifestReport? VerificationReport);

public sealed class MediaIntakeExecutionService
{
    private readonly MediaIntakeManifestService _manifestService;
    private readonly LocalCatalogRefreshService _refreshService;

    public MediaIntakeExecutionService(CatalogStore store, MediaIntakeRuntimeOptions? runtimeOptions = null)
    {
        runtimeOptions ??= new MediaIntakeRuntimeOptions();
        _manifestService = new MediaIntakeManifestService(store, runtimeOptions.IntakeRoot);
        _refreshService = new LocalCatalogRefreshService(store, runtimeOptions);
    }

    public MediaIntakePreview Preview(string sourcePath) => _manifestService.Preview(sourcePath);

    public async Task<MediaIntakeExecutionOutcome> ExecuteAsync(
        string sourcePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        MediaIntakePreview? preview = null;
        string? backupDirectory = null;
        MediaIntakeImportOutcome? importOutcome = null;
        MediaIntakeManifestReport? verification = null;
        var stage = "VALIDATION";

        try
        {
            progress?.Report("Validating EasyFlix media handoff...");
            preview = _manifestService.Preview(sourcePath);
            if (!preview.CanImport)
            {
                var message = string.Join(Environment.NewLine, preview.Errors);
                return Finish(
                    sourcePath,
                    MediaImportFailureKind.Validation,
                    stage,
                    message,
                    preview,
                    backupDirectory,
                    importOutcome,
                    verification);
            }

            stage = "BACKUP";
            progress?.Report("Creating a safe local catalog backup...");
            backupDirectory = await _manifestService.CreateBackupAsync(cancellationToken);

            stage = "REFRESH_BEFORE_IMPORT";
            progress?.Report("Refreshing local media before import...");
            await _refreshService.RefreshAsync(progress, cancellationToken);

            stage = "IMPORT";
            progress?.Report("Applying supplied metadata, artwork, provider IDs, and shortcuts...");
            importOutcome = await _manifestService.ImportHandoffAsync(sourcePath, cancellationToken);

            stage = "REFRESH_AFTER_IMPORT";
            progress?.Report("Refreshing EasyFlix again before final verification...");
            await _refreshService.RefreshAsync(progress, cancellationToken);

            stage = "VERIFY";
            progress?.Report("Verifying every requested media entry...");
            verification = await _manifestService.ApplyDocumentAsync(preview.Manifest, cancellationToken);
            if (verification.Summary.Unresolved > 0)
            {
                return Finish(
                    sourcePath,
                    MediaImportFailureKind.Verification,
                    stage,
                    $"{verification.Summary.Unresolved} media intake entr{(verification.Summary.Unresolved == 1 ? "y is" : "ies are")} unresolved after final verification.",
                    preview,
                    backupDirectory,
                    importOutcome,
                    verification);
            }

            return Finish(
                sourcePath,
                MediaImportFailureKind.None,
                "COMPLETE",
                "Every requested media entry passed final EasyFlix verification.",
                preview,
                backupDirectory,
                importOutcome,
                verification);
        }
        catch (Exception ex)
        {
            var kind = stage switch
            {
                "VALIDATION" => MediaImportFailureKind.Validation,
                "VERIFY" => MediaImportFailureKind.Verification,
                "BACKUP" or "REFRESH_BEFORE_IMPORT" or "IMPORT" or "REFRESH_AFTER_IMPORT"
                    => MediaImportFailureKind.Import,
                _ => MediaImportFailureKind.Internal
            };

            return Finish(
                sourcePath,
                kind,
                stage,
                ex.Message,
                preview,
                backupDirectory,
                importOutcome,
                verification);
        }
    }

    private MediaIntakeExecutionOutcome Finish(
        string sourcePath,
        MediaImportFailureKind failureKind,
        string stage,
        string message,
        MediaIntakePreview? preview,
        string? backupDirectory,
        MediaIntakeImportOutcome? importOutcome,
        MediaIntakeManifestReport? verification)
    {
        var imported = preview?.NewEntries ?? 0;
        var updated = preview?.Updates ?? 0;
        var unresolved = verification?.Summary.Unresolved
            ?? (failureKind == MediaImportFailureKind.Verification
                ? importOutcome?.Report.Summary.Unresolved ?? preview?.Manifest.Entries.Count ?? 0
                : 0);

        var reportPath = BuildFinalReportPath(sourcePath);
        var command = new MediaImportCommandResult(
            failureKind,
            stage,
            imported,
            updated,
            unresolved,
            reportPath);

        try
        {
            WriteFinalReport(
                reportPath,
                sourcePath,
                command,
                message,
                preview,
                backupDirectory,
                importOutcome,
                verification);
            return new(command, message, preview, backupDirectory, importOutcome, verification);
        }
        catch (Exception reportError)
        {
            var failed = new MediaImportCommandResult(
                MediaImportFailureKind.Internal,
                "REPORT",
                imported,
                updated,
                unresolved,
                null);
            return new(
                failed,
                $"{message} Final report could not be written: {reportError.Message}",
                preview,
                backupDirectory,
                importOutcome,
                verification);
        }
    }

    private string BuildFinalReportPath(string sourcePath)
    {
        var directory = Path.Combine(_manifestService.IntakeRootPath, "reports");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
            name = "handoff";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return Path.Combine(
            directory,
            $"{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}__{name}__final.txt");
    }

    private static void WriteFinalReport(
        string reportPath,
        string sourcePath,
        MediaImportCommandResult command,
        string message,
        MediaIntakePreview? preview,
        string? backupDirectory,
        MediaIntakeImportOutcome? importOutcome,
        MediaIntakeManifestReport? verification)
    {
        var builder = new StringBuilder();
        builder.AppendLine("EASYFLIX MEDIA IMPORT EXECUTION REPORT");
        builder.Append(MediaImportCommandContract.FormatFinalBlock(command));
        builder.AppendLine($"SOURCE={sourcePath}");
        builder.AppendLine($"BACKUP={backupDirectory ?? "<none>"}");
        builder.AppendLine($"SOURCE_RECORD={importOutcome?.SourceRecordPath ?? "<none>"}");
        builder.AppendLine($"IMPORT_REPORT={importOutcome?.ReportPath ?? "<none>"}");
        builder.AppendLine($"MESSAGE={message.Replace("\r", " ").Replace("\n", " ")}");
        if (preview is not null)
        {
            builder.AppendLine($"FORMAT={preview.Format}");
            builder.AppendLine($"SOURCE_ENTRIES={preview.Manifest.Entries.Count}");
            builder.AppendLine($"LOGICAL_TITLES={preview.LogicalTitles}");
            builder.AppendLine($"WARNINGS={preview.Warnings.Count}");
            builder.AppendLine($"VALIDATION_ERRORS={preview.Errors.Count}");
        }

        if (verification is not null)
        {
            builder.AppendLine($"VERIFIED={verification.Summary.Applied}");
            builder.AppendLine($"VERIFICATION_CHANGES={verification.Summary.Changed}");
            builder.AppendLine();
            foreach (var item in verification.Results)
                builder.AppendLine(
                    $"{(item.Applied ? "PASS" : "UNRESOLVED")} | {item.Title} | {item.MediaPath}"
                    + (string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : " | " + item.Reason));
        }

        File.WriteAllText(reportPath, builder.ToString(), new UTF8Encoding(false));
    }
}