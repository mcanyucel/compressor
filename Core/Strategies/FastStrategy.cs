using Core.Compression;
using Core.Models;

namespace Core.Strategies;

/// <summary>
/// No FSW consumption, does not check the cancellation token between individual files.Intended for offline or disconnected drives where nothing
/// is actively touching the source files. 
/// </summary>
public sealed class FastStrategy : ICompressionStrategy
{
    public Task CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct) =>
        CompressionHelpers.CompressAsync(item, checkCancellationBetweenFiles: false, ct);

}