using System.Text.Json;
using System.IO;
using System.Security.Cryptography;

namespace EasyGameTranslator;

public sealed class UserSettings
{
    public double FontSize { get; set; } = 26;
    public string SourceLanguage { get; set; } = "en";
    public string? EncryptedDeepLApiKey { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyGameTranslator",
        "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath)) ?? new UserSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }

        return new UserSettings();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string GetDeepLApiKey()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(EncryptedDeepLApiKey)) return string.Empty;
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(EncryptedDeepLApiKey), null, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException) { return string.Empty; }
        catch (FormatException) { return string.Empty; }
    }

    public void SetDeepLApiKey(string value)
    {
        EncryptedDeepLApiKey = string.IsNullOrWhiteSpace(value)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser));
    }
}
