using Core.Compression;
using Core.Models;

namespace Core.Strategies;

/// <summary>
/// Checks the cancellation token between every file, making it responsive to FSW events from the <c>JobExecutor</c>.
/// Intended for live machines where source files may change during execution.
/// </summary>
public sealed class SafeStrategy : ICompressionStrategy
{
    public Task CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct) => 
        CompressionHelpers.CompressAsync(item, checkCancellationBetweenFiles: true, ct);
}