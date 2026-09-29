using System.Windows;

namespace Nebb.DevManager;

public partial class GitCommitDialog : Window
{
    public GitCommitDialog(string branch, string baseBranch, string action, string changes)
    {
        InitializeComponent();
        SummaryText.Text = action switch
        {
            "병합" => $"{branch}의 아래 변경을 모두 커밋한 뒤 {baseBranch}에 병합하고 origin/{baseBranch}에 푸시합니다. 병합 과정에서 작업 브랜치도 원격에 푸시됩니다.",
            "푸시" => $"{branch}의 아래 변경을 모두 커밋한 뒤 해당 브랜치를 origin에 푸시합니다.",
            _ => $"{branch}의 아래 변경을 모두 로컬 커밋합니다. 원격 푸시는 별도 메뉴에서 실행할 수 있습니다."
        };
        ChangesText.Text = changes.TrimEnd();
        ConfirmButton.Content = action == "커밋" ? "커밋" : $"커밋 후 {action}";
        Loaded += (_, _) => MessageText.Focus();
    }

    public string CommitMessage => MessageText.Text.Trim();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (CommitMessage.Length == 0)
        {
            MessageBox.Show(this, "커밋 메시지를 입력하세요.", "Git 커밋",
                MessageBoxButton.OK, MessageBoxImage.Information);
            MessageText.Focus();
            return;
        }
        DialogResult = true;
    }
}
