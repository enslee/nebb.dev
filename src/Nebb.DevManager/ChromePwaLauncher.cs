using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Nebb.DevManager;

internal static class ChromePwaLauncher
{
    public const string Address = "http://127.0.0.1:7878/";

    public static bool Open()
    {
        var chrome = FindChrome() ?? throw new InvalidOperationException("Google Chrome을 찾을 수 없습니다.");
        var start = new ProcessStartInfo(chrome) { UseShellExecute = true };
        var profile = FindInstalledProfile();
        if (profile is null)
            start.ArgumentList.Add(Address);
        else
        {
            start.ArgumentList.Add($"--profile-directory={profile}");
            start.ArgumentList.Add($"--app-id={AppId(Address)}");
        }
        Process.Start(start);
        return profile is not null;
    }

    private static string? FindChrome() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists);

    private static string? FindInstalledProfile()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data");
        if (!Directory.Exists(userData)) return null;
        var appId = AppId(Address);
        return Directory.EnumerateDirectories(userData)
            .Select(Path.GetFileName)
            .Where(name => name == "Default" || name?.StartsWith("Profile ", StringComparison.Ordinal) == true)
            .OrderBy(name => name == "Default" ? 0 : 1)
            .FirstOrDefault(name => Directory.Exists(Path.Combine(userData, name!, "Web Applications", "_crx_" + appId)) ||
                                    Directory.Exists(Path.Combine(userData, name!, "Web Applications", appId)));
    }

    // Chromium derives an installed web app ID by hashing its manifest ID twice.
    // The manifest ID is the fixed start URL for this app.
    internal static string AppId(string manifestId)
    {
        var first = SHA256.HashData(Encoding.UTF8.GetBytes(manifestId));
        var second = SHA256.HashData(first);
        const string alphabet = "abcdefghijklmnop";
        var id = new char[32];
        for (var i = 0; i < 16; i++)
        {
            id[i * 2] = alphabet[second[i] >> 4];
            id[i * 2 + 1] = alphabet[second[i] & 15];
        }
        return new string(id);
    }
}
