using System.Windows;
using System.Windows.Controls;

namespace Nebb.DevManager;

internal sealed record ShutdownEntry(string RepositoryName, RunProfileManager Manager,
    ProcessInstanceView Instance)
{
    public string Display => $"{RepositoryName} / {Instance.Record.WorktreePath} / " +
        $"{Instance.Record.ProfileName} / {Instance.Record.ItemName} " +
        $"({Instance.Record.Status})";
}

internal enum ShutdownChoice { Keep, StopSelected, StopAll }

public partial class ProcessShutdownDialog : Window
{
    internal ShutdownChoice Choice { get; private set; }
    internal IReadOnlyList<ShutdownEntry> SelectedEntries { get; private set; } = [];

    internal ProcessShutdownDialog(IReadOnlyList<ShutdownEntry> entries)
    {
        InitializeComponent();
        RunningList.ItemsSource = entries;
        RunningList.SelectionChanged += (_, _) =>
            StopSelectedButton.IsEnabled = RunningList.SelectedItems.Count > 0;
        StopSelectedButton.IsEnabled = false;
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Choice = ShutdownChoice.Keep;
        DialogResult = true;
    }

    private void StopSelected_Click(object sender, RoutedEventArgs e)
    {
        Choice = ShutdownChoice.StopSelected;
        SelectedEntries = RunningList.SelectedItems.Cast<ShutdownEntry>().ToArray();
        DialogResult = true;
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        Choice = ShutdownChoice.StopAll;
        SelectedEntries = RunningList.Items.Cast<ShutdownEntry>().ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
