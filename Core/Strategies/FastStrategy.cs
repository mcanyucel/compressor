using Core.Compression;
using Core.Models;
using Core.SevenZip;

namespace Core.Strategies;

/// <summary>
/// No FSW consumption. Intended for offline or disconnected drives where nothing is actively touching the source files.
/// Still honours the item's cancellation token (per-item cancel and the global stop button).
/// </summary>
public sealed class FastStrategy(SevenZipRunner runner) : ICompressionStrategy
{
    public Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct) =>
        CompressionHelpers.CompressAsync(item, config, runner, ct);
}
