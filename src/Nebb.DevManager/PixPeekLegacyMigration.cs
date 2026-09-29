using System.IO;

namespace Nebb.DevManager;

// Compatibility import only. All migrated runs use the ordinary Run Profile launcher.
internal static class PixPeekLegacyMigration
{
    public static void Ensure(string repositoryPath, IReadOnlyList<Worktree> worktrees,
        CommandSettingsStore store)
    {
        if (!PixPeekRepository.IsPixPeekRepository(repositoryPath)) return;
        var settings = store.Load();
        var main = worktrees.FirstOrDefault(item => item.Branch is "main" or "master")?.Path
            ?? repositoryPath;
        var changed = false;
        foreach (var worktree in worktrees)
        {
            if (!settings.LegacyPixPeekMigratedWorktrees.Add(worktree.Path)) continue;
            changed = true;
            if (settings.DefaultProfiles.Count != 0 ||
                settings.ProfileOverrides.ContainsKey(worktree.Path)) continue;
            var buildId = Guid.NewGuid().ToString("N");
            var dataDirectory = Path.Combine(main, ".pixpeek-m1-data");
            settings.ProfileOverrides[worktree.Path] = [new RunProfile
            {
                Id = "legacy-pixpeek-development", Name = "PixPeek Development",
                Items = [
                    new RunItem
                    {
                        Id = buildId, Name = "Build", Kind = RunItemKind.ShellScript,
                        Lifecycle = RunLifecycle.OneShot, Shell = RunShell.Cmd,
                        Script = "@echo off\r\n" +
                            "if not exist node_modules\\.bin\\tsc.cmd call npm.cmd ci\r\n" +
                            "if errorlevel 1 exit /b %errorlevel%\r\n" +
                            "if not exist node_modules\\.bin\\vite.cmd call npm.cmd ci\r\n" +
                            "if errorlevel 1 exit /b %errorlevel%\r\n" +
                            $"if not exist \"{dataDirectory}\" mkdir \"{dataDirectory}\"\r\n" +
                            "call npm.cmd run build\r\n"
                    },
                    new RunItem
                    {
                        Id = Guid.NewGuid().ToString("N"), Name = "Full PWA",
                        Kind = RunItemKind.Process, Lifecycle = RunLifecycle.LongRunning,
                        Command = "npm.cmd", Arguments = "run start", DependsOn = [buildId],
                        Readiness = new RunReadiness { Type = RunReadinessKind.Port,
                            Port = 7878, TimeoutSeconds = 60 },
                        Environment = [
                            new EnvironmentEntry("PIXPEEK_PORT", "7878"),
                            new EnvironmentEntry("PIXPEEK_FULL", "1"),
                            new EnvironmentEntry("PIXPEEK_DATA_DIR", dataDirectory),
                            new EnvironmentEntry("PIXPEEK_WEB_DIR", Path.Combine(worktree.Path, "dist"))
                        ]
                    }
                ]
            }];
        }
        if (changed) store.Save(settings);
    }
}
