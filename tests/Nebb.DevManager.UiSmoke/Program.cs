using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Nebb.DevManager;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
var root = Path.Combine(Path.GetTempPath(), "NebbCommandUiSmoke", Guid.NewGuid().ToString("N"));
var worktree = Path.Combine(root, "branch");
var storage = Path.Combine(root, "settings");
Directory.CreateDirectory(worktree);
File.WriteAllText(Path.Combine(root, "package.json"), """{"scripts":{"dev":"vite"}}""");
File.WriteAllText(Path.Combine(worktree, "package.json"), """{"scripts":{"dev":"vite"}}""");

try
{
    var store = new CommandSettingsStore(root, storage);
    RunDialog(root, root, storage, dialog =>
    {
        dialog.CandidateList.SelectedIndex = 0;
        Click(dialog.ChooseCandidateButton);
        dialog.CommandBox.Text = "npm-custom";
        dialog.EnvironmentBox.Text = "PORT=3000";
        dialog.KindChoice.SelectedIndex = 1;
        Click(dialog.ManualButton);
        dialog.NameBox.Text = "Manual test";
        dialog.CommandBox.Text = "custom-test";
        Click(dialog.SaveButton);
    });
    if (store.Load().Default.Run?.Command != "npm-custom") throw new Exception("Default UI save failed");
    if (store.Load().Default.Test?.Command != "custom-test") throw new Exception("Manual test UI save failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.CopyDefaultButton);
        dialog.CommandBox.Text = "npm-branch";
        Click(dialog.SaveButton);
    });
    if (store.Load().Effective(worktree, CommandKind.Run)?.Command != "npm-branch")
        throw new Exception("Override UI save failed");

    RunDialog(root, worktree, storage, dialog =>
    {
        dialog.ScopeChoice.SelectedIndex = 1;
        Click(dialog.ClearButton);
        Click(dialog.SaveButton);
    });
    if (store.Load().Effective(worktree, CommandKind.Run)?.Command != "npm-custom")
        throw new Exception("Override UI reset failed");

    File.Delete(Path.Combine(root, "package.json"));
    RunDialog(root, root, storage, dialog =>
    {
        if (!dialog.StateText.Text.Contains("원본 후보 없음", StringComparison.Ordinal))
            throw new Exception("Missing source message absent: " + dialog.StateText.Text +
                " / " + store.Load().Default.Run?.CandidateId);
        Click(dialog.SaveButton);
    });
    if (store.Load().Default.Run?.Command != "npm-custom")
        throw new Exception("Missing source removed saved command");
    Console.WriteLine("PASS: WPF candidate selection, edit, save, override, reset, stale source");
}
finally
{
    var basePath = Path.Combine(Path.GetTempPath(), "NebbCommandUiSmoke") + Path.DirectorySeparatorChar;
    if (root.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
    }

static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));

static void RunDialog(string root, string worktree, string storage,
    Action<CommandSettingsDialog> action)
{
    var dialog = new CommandSettingsDialog(root, worktree, storage)
    {
        ShowActivated = false,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -10000,
        Top = -10000
    };
    Exception? failure = null;
    var started = DateTime.UtcNow;
    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
    timer.Tick += (_, _) =>
    {
        if (!dialog.RefreshCandidatesButton.IsEnabled ||
            (dialog.CandidateList.Items.Count == 0 && File.Exists(Path.Combine(root, "package.json"))))
        {
            if (DateTime.UtcNow - started < TimeSpan.FromSeconds(10)) return;
            failure = new TimeoutException("Candidate scan did not finish");
            timer.Stop();
            dialog.Close();
            return;
        }
        timer.Stop();
        try { action(dialog); }
        catch (Exception error) { failure = error; dialog.Close(); }
    };
    timer.Start();
    dialog.ShowDialog();
    timer.Stop();
    if (failure is not null) throw failure;
}
}
