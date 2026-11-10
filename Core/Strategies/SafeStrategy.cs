using Core.Compression;
using Core.Models;
using Core.SevenZip;

namespace Core.Strategies;

/// <summary>
/// Intended for live machines where source files may change during execution (v2).
/// Compression is the same as <see cref="FastStrategy"/>; the difference will be the executor's FSW handler
/// cancelling <see cref="JobItem.Cts"/>, which kills the item's 7-Zip process.
/// </summary>
public sealed class SafeStrategy(SevenZipRunner runner) : ICompressionStrategy
{
    public Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct) =>
        CompressionHelpers.CompressAsync(item, config, runner, ct);
}
