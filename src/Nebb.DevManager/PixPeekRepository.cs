using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Nebb.DevManager;

internal static class PixPeekRepository
{
    private static readonly string SavedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Nebb", "DevManager", "pixpeek-repository.txt");

    public static string? Resolve(string[] arguments)
    {
        if (arguments.Length > 0)
        {
            if (arguments.Length != 2 || arguments[0] != "--repository")
                throw new ArgumentException("사용법: Nebb.DevManager.exe [--repository <PixPeek 저장소 경로>]");
            var explicitPath = Path.GetFullPath(arguments[1]);
            if (!IsPixPeekRepository(explicitPath))
                throw new ArgumentException($"PixPeek 저장소를 찾을 수 없습니다: {explicitPath}");
            Save(explicitPath);
            return explicitPath;
        }

        var currentRepository = FindInParents(Environment.CurrentDirectory);
        if (currentRepository is not null)
        {
            Save(currentRepository);
            return currentRepository;
        }

        try
        {
            if (File.Exists(SavedPath))
            {
                var saved = File.ReadAllText(SavedPath).Trim();
                if (IsPixPeekRepository(saved)) return Path.GetFullPath(saved);
            }
        }
        catch (IOException) { /* Ask for the repository again. */ }
        catch (UnauthorizedAccessException) { /* Ask for the repository again. */ }

        while (true)
        {
            var picker = new OpenFolderDialog
            {
                Title = "PixPeek 저장소 폴더 선택"
            };
            if (picker.ShowDialog() != true) return null;
            if (IsPixPeekRepository(picker.FolderName))
            {
                var selected = Path.GetFullPath(picker.FolderName);
                Save(selected);
                return selected;
            }
            MessageBox.Show(
                "PixPeek 저장소의 루트 폴더를 선택하세요.",
                "PixPeek Dev Manager", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static string? FindInParents(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (IsPixPeekRepository(directory.FullName)) return directory.FullName;
        return null;
    }

    private static bool IsPixPeekRepository(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
        (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))) &&
        File.Exists(Path.Combine(path, "package.json")) &&
        File.Exists(Path.Combine(path, "src", "PixPeek.Server", "PixPeek.Server.csproj"));

    private static void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SavedPath)!);
        File.WriteAllText(SavedPath, path);
    }
}
