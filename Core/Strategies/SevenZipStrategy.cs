using Core.Compression;
using Core.Models;
using Core.SevenZip;

namespace Core.Strategies;

/// <summary>
/// Compresses an item by running 7-Zip. Cancelling the token (per-item cancel, the global stop button,
/// or, in v2, the file-watcher handler cancelling <see cref="JobItem.Cts"/>) kills the 7-Zip process.
/// </summary>
public sealed class SevenZipStrategy(SevenZipRunner runner) : ICompressionStrategy
{
    public Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct) =>
        CompressionHelpers.CompressAsync(item, config, runner, ct);
}
