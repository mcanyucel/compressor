using Core.Models;

namespace Core.Strategies;

/// <summary>
/// Defines how a single <see cref="JobItem"/> is compressed.
/// Pre-flight and FSW ahdnling live outside this interface; strategies only differ in their compression behavior.
/// </summary>
public interface ICompressionStrategy
{
    /// <summary>
    /// Compress the given item to its <see cref="JobItem.OutputPath"/>. Writes to a <c>.7z.tmp</c> file and renames in completion.
    /// Throws <see cref="OperationCanceledException"/> on cancellation; the executor cleans up the <c>.tmp</c> file. 
    /// </summary>
    /// <param name="item"></param>
    /// <param name="config"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    Task CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct);
}