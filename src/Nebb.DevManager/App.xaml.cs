using System.Windows;

namespace Nebb.DevManager;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private bool ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        instanceMutex = new Mutex(initiallyOwned: true, "Local\\Nebb.DevManager", out var createdNew);
        ownsMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("Dev Manager가 이미 열려 있습니다.",
                "Dev Manager", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        try
        {
            var catalog = new RepositoryCatalog();
            catalog.Load();
            string? selectedRepository = null;
            if (e.Args.Length > 0)
            {
                if (e.Args.Length != 2 || e.Args[0] != "--repository")
                    throw new ArgumentException("사용법: Nebb.DevManager.exe [--repository <Git 저장소 경로>]");
                selectedRepository = catalog.Add(e.Args[1]).Path;
            }
            MainWindow = new MainWindow(catalog, selectedRepository);
            MainWindow.Show();
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Dev Manager",
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
