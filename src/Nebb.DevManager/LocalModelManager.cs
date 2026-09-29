using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nebb.DevManager;

internal sealed record ReadyLocalModel(LocalModel Model, string Path);

internal sealed class LocalModelManager
{
    private sealed record Installation(string ModelId, string Sha256, long FileSize);
    private sealed record Selection(string ModelId);

    private static readonly HttpClient DownloadClient = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private readonly string storageDirectory;

    public LocalModelManager(string? storageDirectory = null)
    {
        this.storageDirectory = storageDirectory ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nebb", "models");
    }

    public ulong PhysicalMemoryBytes
    {
        get
        {
            var memory = new MemoryStatusEx();
            return GlobalMemoryStatusEx(memory) ? memory.TotalPhysical : 0;
        }
    }

    public LocalModel RecommendedModel => LocalModelCatalog.Recommend(
        PhysicalMemoryBytes, Environment.ProcessorCount);

    public string GetModelPath(LocalModel model) => System.IO.Path.Combine(
        storageDirectory, model.Id, model.FileName);

    public bool IsInstalled(LocalModel model)
    {
        var path = GetModelPath(model);
        var receiptPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "install.json");
        if (!File.Exists(path) || !File.Exists(receiptPath) ||
            new FileInfo(path).Length != model.FileSize) return false;
        try
        {
            var receipt = JsonSerializer.Deserialize<Installation>(File.ReadAllText(receiptPath));
            return receipt?.ModelId == model.Id && receipt.Sha256 == model.Sha256 &&
                   receipt.FileSize == model.FileSize;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    public ReadyLocalModel? GetSelectedModel()
    {
        var selectionPath = System.IO.Path.Combine(storageDirectory, "selection.json");
        if (!File.Exists(selectionPath)) return null;
        try
        {
            var selection = JsonSerializer.Deserialize<Selection>(File.ReadAllText(selectionPath));
            var model = LocalModelCatalog.All.FirstOrDefault(item => item.Id == selection?.ModelId);
            return model is not null && IsInstalled(model)
                ? new ReadyLocalModel(model, GetModelPath(model)) : null;
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void Select(LocalModel model)
    {
        if (!IsInstalled(model))
            throw new InvalidOperationException("먼저 모델을 설치하세요.");
        Directory.CreateDirectory(storageDirectory);
        var path = System.IO.Path.Combine(storageDirectory, "selection.json");
        WriteJsonAtomically(path, new Selection(model.Id));
    }

    public async Task InstallAsync(LocalModel model, IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        if (IsInstalled(model)) return;
        var target = GetModelPath(model);
        var directory = System.IO.Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var partial = target + ".part";
        if (File.Exists(partial)) File.Delete(partial);
        var available = new DriveInfo(System.IO.Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (available < model.FileSize)
            throw new IOException($"설치 드라이브의 빈 공간이 부족합니다. {model.SizeText} 이상 필요합니다.");

        try
        {
            using var response = await DownloadClient.GetAsync(model.DownloadUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != model.FileSize)
                throw new InvalidDataException("배포 파일 크기가 예상과 다릅니다.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(partial, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    received += count;
                    if (received > model.FileSize)
                        throw new InvalidDataException("배포 파일 크기가 예상보다 큽니다.");
                    await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    hash.AppendData(buffer, 0, count);
                    progress.Report((double)received / model.FileSize);
                }
                await destination.FlushAsync(cancellationToken);
            }
            if (received != model.FileSize ||
                !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(model.Sha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("다운로드한 모델의 크기 또는 SHA-256 검증에 실패했습니다.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, target, overwrite: true);
            var receipt = System.IO.Path.Combine(directory, "install.json");
            WriteJsonAtomically(receipt, new Installation(model.Id, model.Sha256, model.FileSize));
            progress.Report(1);
        }
        catch
        {
            if (File.Exists(partial)) File.Delete(partial);
            throw;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
