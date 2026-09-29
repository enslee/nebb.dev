using System.Diagnostics;
using System.Text;

namespace Nebb.DevManager;

internal static class ProcessCommandLine
{
    public static string? Read(int pid)
    {
        // Win32_Process does not expose a working directory, but it does expose
        // the command line needed to verify Nebb's persisted root process.
        var script = $"(Get-CimInstance Win32_Process -Filter 'ProcessId = {pid}' -ErrorAction Stop).CommandLine";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return null;
        }
        return process.ExitCode == 0 ? output.GetAwaiter().GetResult().Trim() : null;
    }
}
