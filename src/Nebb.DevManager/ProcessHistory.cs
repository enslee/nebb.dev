using System.IO;
using System.Text.Json;

namespace Nebb.DevManager;

internal static class ProcessHistory
{
    private const long MaximumLogs = 1_073_741_824;
    private static DateTime lastPruneUtc;
    private static readonly object Sync = new();

    public static void Clean(string repositoryStateRoot, bool clearAll)
    {
        var globalRoot = Path.GetFullPath(Path.GetDirectoryName(repositoryStateRoot)!);
        if (!Directory.Exists(globalRoot)) return;
        lock (Sync)
        {
            if (!clearAll && DateTime.UtcNow - lastPruneUtc < TimeSpan.FromHours(1)) return;
            lastPruneUtc = DateTime.UtcNow;
            var entries = new List<(RunInstanceRecord Record, string Folder)>();
            foreach (var file in Directory.EnumerateFiles(globalRoot, "instance.json",
                         SearchOption.AllDirectories))
            {
                try
                {
                    var record = JsonSerializer.Deserialize<RunInstanceRecord>(File.ReadAllText(file));
                    if (record is not null) entries.Add((record, Path.GetDirectoryName(file)!));
                }
                catch (Exception error) when (error is IOException or JsonException) { }
            }
            var now = DateTime.UtcNow;
            foreach (var entry in entries.ToArray())
            {
                var scoped = Inside(repositoryStateRoot, entry.Folder);
                var finished = Finished(entry.Record);
                if ((!clearAll || !scoped) &&
                    (!finished || now - (entry.Record.EndedUtc ?? entry.Record.StartedUtc)
                        < TimeSpan.FromDays(30))) continue;
                if (!finished || !Inside(globalRoot, entry.Folder)) continue;
                try
                {
                    Directory.Delete(entry.Folder, recursive: true);
                    var latest = Path.Combine(Path.GetDirectoryName(entry.Folder)!, "latest.json");
                    if (File.Exists(latest) &&
                        JsonSerializer.Deserialize<RunInstanceRecord>(File.ReadAllText(latest))?
                            .InstanceId == entry.Record.InstanceId) File.Delete(latest);
                    entries.Remove(entry);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            var logs = entries.SelectMany(entry => new[] { "stdout.log", "stderr.log" }
                .Select(name => (entry.Record, Path: Path.Combine(entry.Folder, name))))
                .Where(value => File.Exists(value.Path))
                .Select(value => (value.Record, value.Path, Size: new FileInfo(value.Path).Length))
                .ToArray();
            var total = logs.Sum(value => value.Size);
            foreach (var log in logs.Where(value => Finished(value.Record))
                         .OrderBy(value => value.Record.EndedUtc ?? value.Record.StartedUtc))
            {
                if (total <= MaximumLogs) break;
                try { File.Delete(log.Path); total -= log.Size; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool Finished(RunInstanceRecord record)
    {
        if (record.JobName is not null)
        {
            try { return !WindowsRunJob.IsRunning(record.JobName); }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        return record.Status is not (RunItemStatus.Starting or RunItemStatus.WaitingReady or
            RunItemStatus.Running or RunItemStatus.Stopping);
    }

    private static bool Inside(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        path = Path.GetFullPath(path);
        return path.StartsWith(root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
