using System.IO;

namespace Nebb.DevManager;

internal static class PixPeekRepository
{
    internal static string? FindInParents(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (IsPixPeekRepository(directory.FullName)) return directory.FullName;
        return null;
    }

    internal static bool IsPixPeekRepository(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) &&
        (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"))) &&
        File.Exists(Path.Combine(path, "package.json")) &&
        File.Exists(Path.Combine(path, "src", "PixPeek.Server", "PixPeek.Server.csproj"));
}
