namespace Nebb.DevManager;

internal sealed record LocalModel(
    string Id,
    string DisplayName,
    string Repository,
    string Revision,
    string FileName,
    long FileSize,
    string Sha256)
{
    public Uri DownloadUri => new(
        $"https://huggingface.co/{Repository}/resolve/{Revision}/{FileName}?download=true");

    public string SizeText => $"{FileSize / 1_000_000_000d:0.0} GB";
}

internal static class LocalModelCatalog
{
    // Pin revisions and Hugging Face LFS hashes so an upstream change cannot silently
    // replace a model that Nebb has already selected.
    public static readonly LocalModel Small = new(
        "qwen3-1.7b-q4-k-m", "Qwen3 1.7B Q4_K_M", "Qwen/Qwen3-1.7B-GGUF",
        "7fb011e9aee6e4dc7adf8430df9ea8de6a466aa3",
        "Qwen3-1.7B-Q4_K_M.gguf", 1_107_408_544,
        "228fb5627f7510b8b3516cdb6435e4b0d2a2bf330fe5b0ab19284a3570a8bb1f");

    public static readonly LocalModel Large = new(
        "qwen3-4b-q4-k-m", "Qwen3 4B Q4_K_M", "Qwen/Qwen3-4B-GGUF",
        "bc640142c66e1fdd12af0bd68f40445458f3869b",
        "Qwen3-4B-Q4_K_M.gguf", 2_497_280_256,
        "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5");

    public static IReadOnlyList<LocalModel> All { get; } = [Small, Large];

    public static LocalModel Recommend(ulong physicalMemoryBytes, int logicalProcessors) =>
        // Windows may report slightly less than 16 GiB on a 16 GB class PC.
        physicalMemoryBytes >= 15UL * 1024 * 1024 * 1024 && logicalProcessors >= 4
            ? Large : Small;
}
