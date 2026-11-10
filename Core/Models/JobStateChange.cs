namespace Core.Models;

/// <summary>
/// Reported via <see cref="IProgress{T}"/> whenever a <see cref="JobItem"/> transitions to a new state.
/// <c>IProgress&lt;T&gt;</c> marshals this to the UI thread automatically when constructed on the UI thread. 
/// </summary>
public sealed class JobStateChange
{
    public required string SourcePath { get; init; }
    public required JobItemState NewState { get; init; }
    /// <summary>
    /// Populated when NewState is NeedsAttention
    /// </summary>
    public string? Reason { get; init; }
}