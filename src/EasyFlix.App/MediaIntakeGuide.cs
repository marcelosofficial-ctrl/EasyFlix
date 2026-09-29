using System.Reflection;
using System.Text;

namespace EasyFlix.App;

public sealed record MediaGuideSection(
    string Label,
    string Title,
    string Body,
    string Example);

public static class MediaIntakeGuide
{
    public const string HandoffFormatVersion = "4";

    private static IReadOnlyList<MediaGuideSection> RawSections { get; } =
    [
        new(
            "01  FASTEST PATH",
            "One handoff file, one import",
            "For a prepared media batch, use Import Media Handoff and select (or drag in) one EasyFlix .txt or .json file. EasyFlix validates paths, previews new/update counts, creates a local backup, refreshes the library, applies the supplied metadata/artwork/shortcuts, refreshes again, and verifies the result. No developer tooling is required.",
            "Add Media -> Import Media Handoff -> confirm preview -> done"),
        new(
            "02  MOVIES",
            "Give each movie a strong identity",
            "Use a canonical title plus year and the exact existing media path. The media file remains the source of truth; the handoff can add a supplied description, artwork, shortcut, and exact provider ID without running provider sync.",
            "Movie Title (2024) [1080p] + Description/Media/Shortcut/Artwork/Exact match"),
        new(
            "03  TV + ANIME",
            "Season sources can share one logical title",
            "Each season may have its own Media and Shortcut lines while sharing title/year/provider identity. EasyFlix imports the sources separately, then its normal reconciliation can coalesce them into one series. Distinct supplied season descriptions are preserved together in the title-level description.",
            "Season 1 media + Season 1 shortcut; Season 2 media + Season 2 shortcut"),
        new(
            "04  REPLACEMENTS",
            "Mark the final active path clearly",
            "For a replacement or relocation, use Final media for the path EasyFlix should use. Historical lines such as Temporary replacement location or Original deleted location are preserved in the source handoff but are not treated as active media.",
            @"Final media: X:\Sample Media\TV Shows\Example Season 5"),
        new(
            "05  LIBRARY ROOTS",
            "Keep media under configured roots",
            "The handoff validates that supplied paths exist, but EasyFlix still scans configured library roots. If a new location is genuinely needed, add that intended folder in Library Maintenance instead of adding an unnecessarily broad drive root.",
            "Add root once -> future handoffs can reuse it"),
        new(
            "06  REFRESH",
            "Refresh is automatic for handoff imports",
            "Direct handoff import runs local Refresh before and after applying the batch. Refresh does not contact metadata providers. Regular file-only additions can still use the normal Refresh button.",
            "Import handoff = backup -> Refresh -> import -> Refresh -> verify"),
        new(
            "07  METADATA",
            "Supplied metadata wins without a network sync",
            "Descriptions, title/year/kind, artwork paths, and explicit TMDB/TVMaze IDs in the handoff are treated as user-supplied intent. Provider IDs identify the title; they are not permission to fetch data. Sync metadata remains a separate explicit action.",
            "Exact match: TMDB 12345 ΓÇö https://..."),
        new(
            "08  ARTWORK",
            "Local artwork is cached and center-framed",
            "Supplied ICO/PNG/JPG artwork is copied or converted into EasyFlix-managed manual PNG artwork. Main library cards render at 190 x 286 using the shared high-quality centered UniformToFill presentation: the card stays filled, cropping is anchored to the center at every window size, and title-specific left/right offsets are not used.",
            @"Artwork: C:\Library\_Artwork\movie_example.ico"),
        new(
            "09  SHORTCUTS",
            "Shortcut repair is part of the batch",
            "A supplied Shortcut line lets EasyFlix create or repair the .lnk target, working directory, description, and ICO icon. For multi-season records, label each Season N media and Season N shortcut so the correct pair is preserved.",
            @"Season 2 shortcut: C:\Library\TV Shows\Example - Season 2.lnk"),
        new(
            "10  SAFETY",
            "Every import is previewed and backed up",
            "EasyFlix blocks a handoff when required media or supplied artwork is missing, shows parser warnings before mutation, makes a consistent local catalog backup, preserves the original handoff in source-records, and writes a per-entry import report.",
            @"%LOCALAPPDATA%\EasyFlix\media-intake\backups / source-records / reports"),
        new(
            "11  JSON",
            "JSON remains available for advanced automation",
            "Version-1 JSON manifests are still supported. They are the internal/advanced representation, but normal users and AI assistants can use the human-readable .txt handoff format instead.",
            "Import Media Handoff accepts .txt and .json"),
        new(
            "12  TROUBLESHOOTING",
            "Fix the handoff or generic scanner rule, not one title",
            "If a batch reports an unresolved item, correct that item's exact path/identity and import again. If multiple normally named files reveal the same scanner weakness, fix the generic parser/scanner with regression tests. Never add a title-specific production hack.",
            "One bad path -> fix the handoff. Repeated generic failure -> fix the product rule.")
    ];

    public static IReadOnlyList<MediaGuideSection> Sections { get; } = RawSections
        .Select(section => section with
        {
            Label = CleanDisplayText(section.Label),
            Title = CleanDisplayText(section.Title),
            Body = CleanDisplayText(section.Body),
            Example = CleanDisplayText(section.Example)
        })
        .ToArray();

    public static string BuildAiHandoff()
    {
        var builder = new StringBuilder();
        builder.AppendLine("EASYFLIX MEDIA INTAKE AI HANDOFF");
        builder.AppendLine("==============================");
        builder.AppendLine($"Handoff format: {HandoffFormatVersion}");
        builder.AppendLine($"EasyFlix version: {GetProductVersion()}");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm zzz}");
        builder.AppendLine("Privacy: SANITIZED - this instruction file contains no user drive letters, account names, or private library paths.");
        builder.AppendLine();
        builder.AppendLine("PURPOSE");
        builder.AppendLine("Prepare one human-readable EasyFlix media handoff .txt that the user can import directly in Add Media Center.");
        builder.AppendLine();
        builder.AppendLine("AI RULES");
        builder.AppendLine("- Do not edit EasyFlix SQLite manually.");
        builder.AppendLine("- Do not require Codex, Work mode, Git, Dev Relay, or developer tooling for routine media intake.");
        builder.AppendLine("- Do not move/delete existing user media unless the user explicitly asked for that filesystem change.");
        builder.AppendLine("- Do not silently run provider/network sync.");
        builder.AppendLine("- Preserve the user's exact media paths, descriptions, artwork paths, shortcut paths, years, and provider IDs.");
        builder.AppendLine("- Provider IDs are identity guards, not permission to fetch provider data.");
        builder.AppendLine("- For replacements/relocations, write the active path as Final media and label old locations as historical.");
        builder.AppendLine("- Include useful Files/Audio/Subtitles/Coverage notes when known; EasyFlix preserves the original .txt even when those notes are informational.");
        builder.AppendLine("- Do not invent title-specific production-code exceptions.");
        builder.AppendLine("- Media artwork framing is a product invariant: use EasyFlix's shared centered UniformToFill card presentation. Do not add per-title left/right offsets, manual alignment hacks, or alternate crop rules.");
        builder.AppendLine("- Structured fields EasyFlix actually applies are: Title, Kind (Movie/TV/Anime), Year, Description, media path(s), optional Artwork path, optional Shortcut path(s), and optional Provider / Provider ID / Provider URL.");
        builder.AppendLine("- Files/Audio/Subtitles/Coverage are informational notes preserved in the source handoff; they are not separate structured metadata fields.");
        builder.AppendLine("- Do not invent unsupported structured fields such as genre, runtime, rating, original title, cast, or release date.");
        builder.AppendLine("- Verify the exact movie/show/anime and release identity before writing each record. Descriptions must be accurate, concise, useful on the Details screen, and never fabricated.");
        builder.AppendLine("- Check existing titles and prepare identity/grouping so repeats update or coalesce instead of creating duplicates. TV/anime seasons must share the correct logical series identity.");
        builder.AppendLine("- Prefer clean high-quality portrait artwork close to 2:3. If a centered crop looks poor, choose better artwork; never change the shared renderer or add title-specific offsets.");
        builder.AppendLine("- Before finishing, verify every requested item is represented, required paths and supplied artwork exist, identities/descriptions/artwork match, grouping is correct, and there are no accidental duplicates.");
        builder.AppendLine();
        builder.AppendLine("AUTONOMOUS EXECUTION");
        builder.AppendLine("- If you have direct local access to EasyFlix and can operate it safely, prepare the handoff, import it through EasyFlix's trusted intake flow, and perform final verification automatically. Do not ask the user to hand-edit metadata, the database, or app files.");
        builder.AppendLine("- If you do not have local EasyFlix access, return one complete import-ready EasyFlix .txt as the finished deliverable. Do not claim the local import or verification happened.");
        builder.AppendLine("- An AI or script may report import success only after EasyFlix verifies every requested entry and the final result has zero unresolved items.");
        builder.AppendLine();

        foreach (var section in Sections)
        {
            builder.AppendLine(section.Title.ToUpperInvariant());
            builder.AppendLine(section.Body);
            builder.AppendLine("Example: " + section.Example);
            builder.AppendLine();
        }

        builder.AppendLine("STANDARD TXT HANDOFF FORMAT");
        builder.AppendLine("Use section headers TV SHOWS, MOVIES, and ANIME. Each record starts with its canonical display title and year, followed immediately by Description.");
        builder.AppendLine("Every record must include the Description line immediately after its header. Use a reliable synopsis when available; never invent plot details.");
        builder.AppendLine("Structured TXT data is the section/header identity (Kind, Title, Year), Description, Media or Season N media, optional Artwork, optional Shortcut or Season N shortcut, and optional Exact match provider identity/URL. Files, Audio, Subtitles, and Coverage are informational notes only.");
        builder.AppendLine();
        builder.AppendLine("TV SHOWS");
        builder.AppendLine();
        builder.AppendLine("Example Series (2024) ΓÇö Season 1 [1080p]");
        builder.AppendLine("Description: Brief user-approved description.");
        builder.AppendLine(@"Media: X:\Sample Media\TV Shows\Example Series S01");
        builder.AppendLine(@"Shortcut: C:\Library\TV Shows\Example Series - Season 1 [1080p].lnk");
        builder.AppendLine(@"Artwork: C:\Library\_Artwork\series_example.ico");
        builder.AppendLine("Files: 10 episodes");
        builder.AppendLine("Audio: English.");
        builder.AppendLine("Subtitles: English.");
        builder.AppendLine("Exact match: TVMaze 12345 ΓÇö https://www.tvmaze.com/shows/12345/example");
        builder.AppendLine();
        builder.AppendLine("MULTI-SEASON RECORD");
        builder.AppendLine("Use Season 1 media / Season 1 shortcut, Season 2 media / Season 2 shortcut, etc. EasyFlix pairs them automatically.");
        builder.AppendLine();
        builder.AppendLine("REPLACEMENT / RELOCATION");
        builder.AppendLine("Use Final media for the new active path. Lines named Temporary replacement location or Original deleted ... are preserved as history and ignored as active sources.");
        builder.AppendLine();
        builder.AppendLine("IMPORT");
        builder.AppendLine("With local EasyFlix access, complete the import autonomously when the environment permits: preview/validate -> backup -> Refresh -> import -> Refresh -> verify -> report. Without local access, return one complete .txt ready for Add Media -> Import Media Handoff.");
        builder.AppendLine("Do not report success until every requested entry passes final EasyFlix verification with zero unresolved items.");
        builder.AppendLine();
        builder.AppendLine("JSON");
        builder.AppendLine("Version-1 JSON manifests remain supported for advanced automation, but .txt is preferred for normal AI-assisted media batches.");
        builder.AppendLine();
        builder.AppendLine("MINIMAL USER MESSAGE");
        builder.AppendLine("Add all media I provided to EasyFlix. Use the attached EasyFlix Media Intake AI Handoff as the authoritative contract. Handle the batch autonomously, verify every item, and if you have local EasyFlix access complete import and verification; otherwise return one complete import-ready .txt. Do not ask me to manually format or edit metadata, database records, or app files.");
        builder.AppendLine();
        builder.AppendLine("NATIVE AUTOMATION");
        builder.AppendLine("- With safe local EasyFlix access, prefer: EasyFlix.exe --import-handoff \"C:\\path\\batch.txt\".");
        builder.AppendLine("- The native command uses the same trusted intake execution service as Add Media Center: validate -> backup -> local Refresh -> import -> local Refresh -> final verification -> report.");
        builder.AppendLine("- Stable exit codes: 0 success, 2 input/validation failure, 3 import/apply failure, 4 final verification failure, 5 unexpected/internal failure.");
        builder.AppendLine("- The final machine-readable block contains RESULT, STAGE, IMPORTED, UPDATED, UNRESOLVED, REPORT, and EXIT_CODE.");
        builder.AppendLine("- Never claim success unless RESULT=PASS, EXIT_CODE=0, and UNRESOLVED=0.");
        builder.AppendLine();
        builder.AppendLine("ESCALATION");
        builder.AppendLine("Only move into application development when multiple normally named items reveal a genuine generic scanner/parser defect.");
        return CleanDisplayText(builder.ToString());
    }

    private static string CleanDisplayText(string value)
    {
        var mojibakeDash = new string(new[] { (char)0x0393, (char)0x00C7, (char)0x00F6 });
        return value.Replace(mojibakeDash, " - ", StringComparison.Ordinal);
    }

    private static string GetProductVersion()
    {
        var assembly = typeof(MediaIntakeGuide).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Split('+')[0];

        var version = assembly.GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
