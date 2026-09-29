using System.Windows;

namespace Nebb.DevManager;

public partial class RunPortConflictDialog : Window
{
    internal RunPortConflictDialog(IEnumerable<string> conflicts)
    {
        InitializeComponent();
        ConflictText.Text = string.Join("\n", conflicts);
    }

    private void RunAnyway_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
