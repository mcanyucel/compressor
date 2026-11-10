using Core.Compression;
using Core.Models;

namespace Core.Strategies;

/// <summary>
/// Defines how a single <see cref="JobItem"/> is compressed.
/// Pre-flight and FSW handling live outside this interface; strategies only differ in their compression behavior.
/// </summary>
public interface ICompressionStrategy
{
    /// <summary>
    /// Compress the given item to its <see cref="JobItem.OutputPath"/>. Writes to a <c>.7z.tmp</c> file and renames it on completion.
    /// Throws <see cref="OperationCanceledException"/> on cancellation, after deleting the <c>.tmp</c> file.
    /// </summary>
    Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct);
}
