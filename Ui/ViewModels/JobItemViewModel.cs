using CommunityToolkit.Mvvm.ComponentModel;
using Core.Models;

namespace Ui.ViewModels;

/// <summary>
/// One row in the Run tab. UI thread only; <see cref="SyncFrom"/> copies the thread-safe <see cref="JobItem"/> fields.
/// </summary>
public partial class JobItemViewModel(JobItem item, string name) : ObservableObject
{
    public JobItem Item { get; } = item;
    public string Name { get; } = name;
    public string SizeText { get; } = Display.Bytes(item.TotalBytes);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    public partial JobItemState State { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? Detail { get; set; }

    public string StateText => State switch
    {
        JobItemState.Pending => "waiting",
        JobItemState.InProgress => "compressing",
        JobItemState.Verified => "verified",
        JobItemState.Done => "done",
        JobItemState.Skipped => "skipped",
        JobItemState.Cancelled => "cancelled",
        JobItemState.NeedsAttention => "needs attention",
        JobItemState.Failed => "failed",
        _ => State.ToString()
    };

    public void SyncFrom()
    {
        State = Item.State;
        long processed = Interlocked.Read(ref Item.BytesProcessed);

        Progress = State is JobItemState.Done or JobItemState.Verified ? 100
            : Item.TotalBytes > 0 ? 100.0 * processed / Item.TotalBytes
            : 0;

        Detail = State switch
        {
            JobItemState.InProgress => $"{Display.Bytes(processed)} of {SizeText}",
            JobItemState.Done when Item.Stats is { } s =>
                $"{Display.Bytes(s.BytesBefore)} → {Display.Bytes(s.BytesAfter)} ({s.CompressionRatio:P0} saved, {Display.Duration(s.Duration)})" +
                (s.FilesDeleted > 0 ? $", {s.FilesDeleted:N0} originals deleted" : ""),
            JobItemState.Skipped => "No files to compress.",
            JobItemState.NeedsAttention or JobItemState.Failed or JobItemState.Cancelled => Item.Error?.Message,
            _ => null
        };
    }
}
