using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace Nebb.DevManager;

public partial class LocalModelDialog : Window
{
    private readonly LocalModelManager manager = new();
    private CancellationTokenSource? installCancellation;

    public LocalModelDialog()
    {
        InitializeComponent();
        ModelChoice.ItemsSource = LocalModelCatalog.All;
        var recommended = manager.RecommendedModel;
        var ram = manager.PhysicalMemoryBytes;
        RecommendationText.Text = ram == 0
            ? $"PC 메모리를 확인할 수 없어 {recommended.DisplayName}를 기본 추천합니다. 직접 바꿀 수 있습니다."
            : $"이 PC: 메모리 {ram / 1024d / 1024 / 1024:0.#} GB, 논리 프로세서 {Environment.ProcessorCount}개\n" +
              $"추천: {recommended.DisplayName} (16 GB급 메모리와 논리 프로세서 4개 이상일 때 4B 추천). 직접 바꿀 수 있습니다.";
        ModelChoice.SelectedItem = manager.GetSelectedModel()?.Model ?? recommended;
        UpdateState();
    }

    private void ModelChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (ModelDetailsText is null || ModelChoice.SelectedItem is not LocalModel model) return;
        var installed = manager.IsInstalled(model);
        var selected = manager.GetSelectedModel();
        ModelDetailsText.Text = $"{model.SizeText} 다운로드 · {(installed ? "설치됨" : "설치되지 않음")}\n" +
                                $"출처: {model.Repository} · Apache-2.0";
        SelectionText.Text = selected is null
            ? "nebb에서 사용할 모델이 아직 선택되지 않았습니다."
            : $"현재 선택한 모델: {selected.Model.DisplayName}\n파일: {selected.Path}";
        InstallButton.Content = installed ? "이 모델 선택" : "설치하고 선택";
        InstallButton.IsEnabled = installCancellation is null;
        ModelChoice.IsEnabled = installCancellation is null;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (ModelChoice.SelectedItem is not LocalModel model || installCancellation is not null) return;
        installCancellation = new CancellationTokenSource();
        CancelInstallButton.IsEnabled = true;
        UpdateState();
        try
        {
            if (!manager.IsInstalled(model))
            {
                DownloadProgress.Value = 0;
                ProgressText.Text = $"{model.DisplayName} 다운로드 중...";
                var progress = new Progress<double>(fraction =>
                {
                    if (installCancellation is null) return;
                    DownloadProgress.Value = fraction * 100;
                    ProgressText.Text = $"다운로드 중: {fraction:P0}";
                });
                await manager.InstallAsync(model, progress, installCancellation.Token);
            }
            manager.Select(model);
            ProgressText.Text = $"{model.DisplayName} 설치와 선택을 완료했습니다.";
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "설치를 취소했습니다.";
            DownloadProgress.Value = 0;
        }
        catch (Exception error)
        {
            ProgressText.Text = "설치 또는 선택에 실패했습니다.";
            MessageBox.Show(this, error.Message, "로컬 모델 준비 실패",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            installCancellation.Dispose();
            installCancellation = null;
            CancelInstallButton.IsEnabled = false;
            UpdateState();
        }
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        CancelInstallButton.IsEnabled = false;
        ProgressText.Text = "설치 취소 중...";
        installCancellation?.Cancel();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (installCancellation is not null)
        {
            installCancellation.Cancel();
            ProgressText.Text = "설치 취소 중... 완료되면 창을 닫을 수 있습니다.";
            e.Cancel = true;
        }
        base.OnClosing(e);
    }
}
