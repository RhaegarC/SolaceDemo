using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SolaceClient;

/// <summary>
/// One file staged for a case. Size is measured when the file is added, so both the number
/// of chunks it will be sent as and whether it is over the ceiling are known before
/// anything is published.
/// </summary>
public sealed class AttachmentItem : INotifyPropertyChanged
{
    private string _status;

    public AttachmentItem(string fullPath, long maxFileBytes, long chunkSizeBytes)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Format = AttachmentTopics.FormatFor(FileName);
        SizeBytes = TryReadLength(fullPath);

        // Split only what has to be split. A file that fits in one message is sent as one,
        // so the common case behaves exactly as it did before chunking existed.
        ChunkCount = chunkSizeBytes > 0 && SizeBytes > chunkSizeBytes
            ? (int)((SizeBytes + chunkSizeBytes - 1) / chunkSizeBytes)
            : 1;

        // The ceiling is exclusive and now applies to the whole file rather than to one
        // message: chunking lifts the per-message limit off a file, but the client still
        // holds it in memory entire, so something has to bound it.
        IsRefused = SizeBytes >= maxFileBytes;
        RefusalReason = IsRefused
            ? $"{SizeText} — over the {FormatBytes(maxFileBytes)} limit"
            : null;

        _status = IsRefused ? "REFUSED" : IsChunked ? $"{ChunkCount:N0} chunks" : "ready";
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Format { get; }
    public long SizeBytes { get; }
    public bool IsRefused { get; }
    public string? RefusalReason { get; }

    /// <summary>How many messages this file will be sent as. One when it is not split.</summary>
    public int ChunkCount { get; }

    public bool IsChunked => ChunkCount > 1;

    public string SizeText => FormatBytes(SizeBytes);

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static long TryReadLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1_048_576
            ? $"{bytes / 1_048_576.0:0.0} MB"
            : $"{bytes / 1024.0:0.0} KB";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
