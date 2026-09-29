using System.IO;

namespace EasyFlix.App.Services;

public static class CatalogLocation
{
    public static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyFlix",
        "catalog",
        "easyflix.db");
}
