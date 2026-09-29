using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nebb.DevManager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0].StartsWith("--start-process-ui-fixture=", StringComparison.Ordinal))
        {
            var repository = args[0]["--start-process-ui-fixture=".Length..];
            var profile = new RunProfile { Id = "process-ui-profile", Name = "UI Development", Items =
                [new RunItem { Id = "process-ui-item", Name = "UI Server",
                    Kind = RunItemKind.ShellScript,
                    Script = "Write-Output 'ui-reattach-ready'; Start-Sleep -Seconds 30" }] };
            new RunProfileManager(repository).RunAllAsync(repository, profile)
                .GetAwaiter().GetResult();
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "NebbProfileUiSmoke", Guid.NewGuid().ToString("N"));
        var worktree = Path.Combine(root, "branch");
        var storage = Path.Combine(root, "settings");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../.git/worktrees/branch");
        Directory.CreateDirectory(Path.Combine(root, "apps", "desktop", "src-tauri"));
        File.WriteAllText(Path.Combine(root, "apps", "desktop", "package.json"),
            """{"scripts":{"dev":"vite","tauri":"tauri"},"devDependencies":{"@tauri-apps/cli":"2.0.0"}}""");
        File.WriteAllText(Path.Combine(root, "apps", "desktop", "src-tauri", "tauri.conf.json"), "{}");
        Directory.CreateDirectory(Path.Combine(worktree, "apps", "desktop", "src-tauri"));
        File.WriteAllText(Path.Combine(worktree, "apps", "desktop", "package.json"),
            """{"scripts":{"dev":"vite","tauri":"tauri"},"devDependencies":{"@tauri-apps/cli":"2.0.0"}}""");
        File.WriteAllText(Path.Combine(worktree, "apps", "desktop", "src-tauri", "tauri.conf.json"), "{}");
        File.WriteAllText(Path.Combine(root, "package.json"), """{"scripts":{"test":"vitest"}}""");
        Directory.CreateDirectory(Path.Combine(root, "server"));
        File.WriteAllText(Path.Combine(root, "server", "compose.yaml"), "services:\n  db:\n    image: postgres\n");
        try
        {
            var store = new CommandSettingsStore(root, storage);
            RunProfileDialog(root, root, storage, dialog =>
            {
                Click(dialog.AddProfileButton);
                dialog.ProfileNameBox.Text = "Mobile Development";
                dialog.CandidateChoice.SelectedItem = dialog.CandidateChoice.Items
                    .OfType<CommandCandidate>().Single(item => item.Name == "Tauri App");
                Click(dialog.AddCandidateButton);
                if (dialog.CommandBox.Text != "npm" || dialog.ArgumentsBox.Text != "run tauri dev" ||
                    dialog.WorkingDirectoryBox.Text != "apps/desktop")
                    throw new Exception("Run candidate was not added to profile");
                Click(dialog.AddScriptButton);
                dialog.NameBox.Text = "Migration";
                dialog.ScriptBox.Text = "Write-Output ready";
                dialog.DependsList.SelectedIndex = 0;
                Click(dialog.SaveButton);
            });
            var saved = store.Load().DefaultProfiles.Single();
            if (saved.Name != "Mobile Development" || saved.Items.Count != 2 ||
                saved.Items[1].DependsOn.Single() != saved.Items[0].Id ||
                saved.Items[1].Kind != RunItemKind.ShellScript)
                throw new Exception("Profile editor did not persist settings");

            RunProfileDialog(root, worktree, storage, dialog =>
            {
                dialog.ScopeChoice.SelectedIndex = 1;
                if (!dialog.ScopeStateText.Text.Contains("상속"))
                    throw new Exception("Inheritance state missing");
                Click(dialog.CopyButton);
                dialog.ProfileNameBox.Text = "Branch Development";
                Click(dialog.SaveButton);
            });
            if (store.Load().EffectiveProfiles(worktree).Single().Name != "Branch Development" ||
                store.Load().DefaultProfiles.Single().Name != "Mobile Development")
                throw new Exception("Worktree snapshot override failed");
            RunProfileDialog(root, worktree, storage, dialog =>
            {
                dialog.ScopeChoice.SelectedIndex = 1;
                Click(dialog.ResetButton);
                Click(dialog.SaveButton);
            });
            if (store.Load().EffectiveProfiles(worktree).Single().Name != "Mobile Development")
                throw new Exception("Worktree override reset failed");

            RunCommandDialog(root, root, storage, dialog =>
            {
                SelectCandidate(dialog, item => item.Kind == CommandKind.Test);
                Click(dialog.ChooseCandidateButton);
                dialog.CommandBox.Text = "test-custom";
                Click(dialog.SaveButton);
            });
            if (store.Load().Default.Test?.Command != "test-custom")
                throw new Exception("Test command editor regressed");
            RunCommandDialog(root, root, storage, dialog =>
            {
                SelectCandidate(dialog, item => item.Kind == CommandKind.Service);
                Click(dialog.ChooseCandidateButton);
                Click(dialog.SaveButton);
            });
            if (store.Load().DefaultServices.Count != 1)
                throw new Exception("Service editor regressed");
            var conflictDialog = new RunPortConflictDialog(["Port 5000: Mobile Development / API"])
            {
                ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000
            };
            RunDialog(conflictDialog, () => conflictDialog.IsLoaded, () =>
            {
                if (!conflictDialog.ConflictText.Text.Contains("Mobile Development / API"))
                    throw new Exception("Port conflict details missing");
                Click(conflictDialog.RunAnywayButton);
            });
            var manager = new RunProfileManager(root, Path.Combine(root, "profile-runs"));
            var shutdownEntries = Enumerable.Range(0, 2).Select(index =>
                new ShutdownEntry("Example", manager, new ProcessInstanceView(
                    new RunInstanceRecord($"instance-{index}", worktree, "profile", $"item-{index}",
                        1234 + index, DateTime.UtcNow, null, RunItemStatus.Running, null, null)
                    { ProfileName = "Development", ItemName = $"Service {index}" }, []))).ToArray();
            var keep = new ProcessShutdownDialog(shutdownEntries)
                { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            RunDialog(keep, () => keep.IsLoaded, () => Click(keep.KeepButton));
            if (keep.Choice != ShutdownChoice.Keep || keep.SelectedEntries.Count != 0)
                throw new Exception("Keep-and-close choice failed");
            var selected = new ProcessShutdownDialog(shutdownEntries)
                { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            RunDialog(selected, () => selected.IsLoaded, () =>
            {
                selected.RunningList.SelectedIndex = 1;
                Click(selected.StopSelectedButton);
            });
            if (selected.Choice != ShutdownChoice.StopSelected ||
                selected.SelectedEntries.Single() != shutdownEntries[1])
                throw new Exception("Selected shutdown choice failed");
            var all = new ProcessShutdownDialog(shutdownEntries)
                { ShowActivated = false, ShowInTaskbar = false, Left = -10000, Top = -10000 };
            RunDialog(all, () => all.IsLoaded, () => Click(all.StopAllButton));
            if (all.Choice != ShutdownChoice.StopAll || all.SelectedEntries.Count != 2)
                throw new Exception("Stop-all shutdown choice failed");
            RunReattachedMainWindow(root);
            Console.WriteLine("PASS: WPF profile editor, shutdown choices, and reattached process controls");
        }
        finally
        {
            var basePath = Path.Combine(Path.GetTempPath(), "NebbProfileUiSmoke") + Path.DirectorySeparatorChar;
            if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void RunReattachedMainWindow(string root)
    {
        var repository = Path.Combine(root, "process-ui-repository");
        Directory.CreateDirectory(repository);
        using (var git = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "init", "-b", "main" }
        })!) git.WaitForExit();
        var profile = new RunProfile { Id = "process-ui-profile", Name = "UI Development", Items =
            [new RunItem { Id = "process-ui-item", Name = "UI Server",
                Kind = RunItemKind.ShellScript,
                Script = "Write-Output 'ui-reattach-ready'; Start-Sleep -Seconds 30" }] };
        var store = new CommandSettingsStore(repository);
        var settings = store.Load();
        settings.DefaultProfiles = [profile];
        store.Save(settings);
        var repositoryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(repository.ToUpperInvariant())))[..20]
            .ToLowerInvariant();
        var appData = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "Nebb", "DevManager");
        var runsPath = Path.Combine(appData, "profile-runs", repositoryHash);
        var settingsPath = Path.Combine(appData, "command-settings", repositoryHash + ".json");
        try
        {
            var starter = new ProcessStartInfo("dotnet")
                { UseShellExecute = false, CreateNoWindow = true };
            starter.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            starter.ArgumentList.Add("--start-process-ui-fixture=" + repository);
            using (var child = Process.Start(starter)!)
            {
                child.WaitForExit();
                if (child.ExitCode != 0) throw new Exception("UI reattach fixture did not start");
            }
            var appAssembly = Assembly.Load("Nebb.DevManager");
            var catalogType = appAssembly.GetType("Nebb.DevManager.RepositoryCatalog", true)!;
            var catalog = Activator.CreateInstance(catalogType,
                [Path.Combine(root, "process-ui-catalog.json")])!;
            catalogType.GetMethod("Add")!.Invoke(catalog, [repository]);
            var mainType = appAssembly.GetType("Nebb.DevManager.MainWindow", true)!;
            var constructor = mainType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                null, [catalogType, typeof(string)], null)!;
            var main = (Window)constructor.Invoke([catalog, repository]);
            main.ShowActivated = false;
            main.ShowInTaskbar = false;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -10000;
            main.Top = -10000;
            var list = (ListView)main.FindName("ProcessList")!;
            var log = (TextBox)main.FindName("LogText")!;
            var started = DateTime.UtcNow;
            var clicked = false;
            Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (DateTime.UtcNow - started > TimeSpan.FromSeconds(18))
                    {
                        var current = new RunProfileManager(repository).GetItemState(repository,
                            profile, profile.Items[0]);
                        throw new TimeoutException($"WPF process control timed out: {current.Status} {current.Reason}");
                    }
                    if (!clicked)
                    {
                        if (list.Items.Count == 0) return;
                        if ((bool)mainType.GetField("busy", BindingFlags.Instance |
                            BindingFlags.NonPublic)!.GetValue(main)!) return;
                        var row = list.Items[0];
                        var display = row.GetType().GetProperty("Display")!.GetValue(row)!.ToString()!;
                        if (!display.Contains("Running")) return;
                        var button = new Button { Tag = row };
                        mainType.GetMethod("ProcessLogs_Click", BindingFlags.Instance |
                            BindingFlags.NonPublic)!.Invoke(main, [button, new RoutedEventArgs()]);
                        if (!log.Text.Contains("ui-reattach-ready"))
                            throw new Exception("WPF process log did not reopen after restart");
                        mainType.GetMethod("ProcessKill_Click", BindingFlags.Instance |
                            BindingFlags.NonPublic)!.Invoke(main, [button, new RoutedEventArgs()]);
                        clicked = true;
                    }
                    else
                    {
                        var current = new RunProfileManager(repository).GetItemState(repository,
                            profile, profile.Items[0]);
                        if (current.Status != RunItemStatus.Stopped) return;
                        if ((bool)mainType.GetField("busy", BindingFlags.Instance |
                            BindingFlags.NonPublic)!.GetValue(main)!) return;
                        timer.Stop();
                        mainType.GetField("closeApproved", BindingFlags.Instance |
                            BindingFlags.NonPublic)!.SetValue(main, true);
                        main.Close();
                    }
                }
                catch (Exception error)
                {
                    failure = error;
                    timer.Stop();
                    var record = new RunProfileManager(repository).ListInstances()
                        .FirstOrDefault()?.Record;
                    if (record?.JobName is not null)
                        try { WindowsRunJob.Terminate(record.JobName); } catch { }
                    mainType.GetField("closeApproved", BindingFlags.Instance |
                        BindingFlags.NonPublic)!.SetValue(main, true);
                    main.Close();
                }
            };
            timer.Start();
            main.ShowDialog();
            timer.Stop();
            if (failure is not null) throw failure;
            if (!clicked) throw new Exception("WPF reattached process was not controlled");
        }
        finally
        {
            Task.Run(() => new RunProfileManager(repository).StopRunningAsync())
                .GetAwaiter().GetResult();
            var runsRoot = Path.GetFullPath(Path.Combine(appData, "profile-runs")) +
                Path.DirectorySeparatorChar;
            if (Path.GetFullPath(runsPath).StartsWith(runsRoot,
                    StringComparison.OrdinalIgnoreCase) && Directory.Exists(runsPath))
                Directory.Delete(runsPath, recursive: true);
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
        }
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

    private static void RunProfileDialog(string root, string worktree, string storage,
        Action<RunProfilesDialog> action)
    {
        var dialog = new RunProfilesDialog(root, worktree, storage)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        RunDialog(dialog, () => dialog.CandidateChoice.Items.Count > 0, () => action(dialog));
    }

    private static void RunCommandDialog(string root, string worktree, string storage,
        Action<CommandSettingsDialog> action)
    {
        var dialog = new CommandSettingsDialog(root, worktree, storage)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000
        };
        RunDialog(dialog, () => dialog.RefreshCandidatesButton.IsEnabled &&
            dialog.CandidateTree.Items.Count > 0, () => action(dialog));
    }

    private static void RunDialog(Window dialog, Func<bool> ready, Action action)
    {
        Exception? failure = null;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (!ready())
            {
                if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10)) return;
                failure = new TimeoutException("Candidate scan did not finish");
                timer.Stop(); dialog.Close(); return;
            }
            timer.Stop();
            try { action(); }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        dialog.ShowDialog();
        timer.Stop();
        if (failure is not null) throw failure;
    }

    private static void SelectCandidate(CommandSettingsDialog dialog,
        Func<CommandCandidate, bool> matches)
    {
        TreeViewItem? Find(IEnumerable<TreeViewItem> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.Tag is CommandCandidate candidate && matches(candidate)) return node;
                var child = Find(node.Items.OfType<TreeViewItem>());
                if (child is not null) return child;
            }
            return null;
        }
        var item = Find(dialog.CandidateTree.Items.OfType<TreeViewItem>())
            ?? throw new Exception("Candidate tree item missing");
        item.IsSelected = true;
    }
}
