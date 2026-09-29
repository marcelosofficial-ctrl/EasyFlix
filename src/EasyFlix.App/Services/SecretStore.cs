using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyFlix.App.Services;

public sealed class SecretStore
{
    public const string TmdbApiKey = "tmdb-api-key";
    public const string SubDlApiKey = "subdl-api-key";
    public const string OpenSubtitlesApiKey = "opensubtitles-api-key";
    public const string OpenSubtitlesUsername = "opensubtitles-username";
    public const string OpenSubtitlesPassword = "opensubtitles-password";

    // Preserve the original entropy so existing DPAPI-encrypted credentials remain decryptable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ReelShelf:v1:CurrentUser");

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyFlix", "secrets.json");

    private readonly string _legacyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReelShelf", "secrets.json");

    public bool Has(string name) => Get(name) is not null;

    public string? Get(string name)
    {
        try
        {
            var values = ReadAll();
            if (!values.TryGetValue(name, out var encoded)) return null;
            var clear = ProtectedData.Unprotect(Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(clear); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        catch { return null; }
    }

    public void Set(string name, string? value)
    {
        var values = ReadAll();
        if (string.IsNullOrWhiteSpace(value)) values.Remove(name);
        else
        {
            var clear = Encoding.UTF8.GetBytes(value.Trim());
            try
            {
                var encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
                values[name] = Convert.ToBase64String(encrypted);
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }

        WriteAll(values);
    }

    private Dictionary<string, string> ReadAll()
    {
        try
        {
            if (TryRead(_path, out var current))
                return current;

            if (TryRead(_legacyPath, out var legacy))
            {
                WriteAll(legacy);
                return legacy;
            }

            return [];
        }
        catch { return []; }
    }

    private static bool TryRead(string path, out Dictionary<string, string> values)
    {
        values = [];
        if (!File.Exists(path))
            return false;

        values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        return true;
    }

    private void WriteAll(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }
}
