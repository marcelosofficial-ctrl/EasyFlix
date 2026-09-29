using EasyFlix.Core.Models;

namespace EasyFlix.Core.Services;

public sealed record LibraryAction(MediaItem Item, string ShortcutPath, bool AlreadyExists);

public sealed class LibraryPlanner
{
    public IReadOnlyList<LibraryAction> BuildPlan(IEnumerable<MediaItem> items, string libraryPath)
    {
        var root = Path.GetFullPath(libraryPath);
        return items.Where(item => item.IsSelected).Select(item =>
        {
            var category = item.Kind switch
            {
                MediaKind.Anime => "Anime Series",
                MediaKind.TvShow => "TV Shows",
                _ => string.Empty
            };
            var folder = string.IsNullOrEmpty(category) ? root : Path.Combine(root, category);
            var safeName = string.Concat(item.SuggestedName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var shortcut = Path.Combine(folder, safeName + ".lnk");
            return new LibraryAction(item, shortcut, File.Exists(shortcut));
        }).ToArray();
    }
}
