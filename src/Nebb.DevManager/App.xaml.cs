using System.Windows;

namespace Nebb.DevManager;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Share the original mutex so the two managers cannot control the same server at once.
        instanceMutex = new Mutex(initiallyOwned: true, "Local\\PixPeek.DevManager", out var createdNew);
        ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("PixPeek 개발 서버 관리 앱이 이미 열려 있습니다.",
                "PixPeek Dev Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        try
        {
            var repositoryPath = PixPeekRepository.Resolve(e.Args);
            if (repositoryPath is null)
            {
                Shutdown();
                return;
            }
            MainWindow = new MainWindow(repositoryPath);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "PixPeek Dev Manager",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
