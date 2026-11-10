using Core.Compression;
using Core.Models;

namespace Core.Strategies;

/// <summary>
/// Defines how a single <see cref="JobItem"/> is compressed and verified.
/// Pre-flight and FSW handling live outside this interface. The executor depends only on this interface,
/// so it can be tested with a fake.
/// </summary>
public interface ICompressionStrategy
{
    /// <summary>
    /// Compress the given item to its <see cref="JobItem.OutputPath"/>. Writes to a <c>.7z.tmp</c> file and renames it on completion.
    /// Throws <see cref="OperationCanceledException"/> on cancellation, after deleting the <c>.tmp</c> file.
    /// </summary>
    Task<CompressionResult> CompressItemAsync(JobItem item, JobConfig config, CancellationToken ct);

    /// <summary>
    /// Check the archive written by a <see cref="CompressionOutcome.Completed"/> compression.
    /// </summary>
    Task<VerificationResult> VerifyItemAsync(JobItem item, CompressionResult compression, CancellationToken ct);
}
