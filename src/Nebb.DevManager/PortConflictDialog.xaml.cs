using System.Windows;

namespace Nebb.DevManager;

public partial class PortConflictDialog : Window
{
    internal PortConflictDialog(PixPeekPortConflictException conflict)
    {
        InitializeComponent();
        ProcessDetails.Text = $"PID: {conflict.ProcessId}\n실행 파일: {conflict.ExecutablePath}";
    }

    private void Terminate_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
