using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace SolaceClient;

/// <summary>
/// One file staged for a case. Size is measured and validated when the file is added,
/// so a file over the limit is marked before anything is published.
/// </summary>
public sealed class AttachmentItem : INotifyPropertyChanged
{
    private string _status;

    public AttachmentItem(string fullPath, long maxAttachmentBytes)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Format = AttachmentTopics.FormatFor(FileName);
        SizeBytes = TryReadLength(fullPath);

        // The cap is exclusive: a file sitting exactly on it is refused, because the
        // broker's limit counts the whole message, not just the payload.
        IsRefused = SizeBytes >= maxAttachmentBytes;
        RefusalReason = IsRefused
            ? $"{SizeText} — over the {FormatBytes(maxAttachmentBytes)} limit"
            : null;

        _status = IsRefused ? "REFUSED" : "ready";
    }

    public string FullPath { get; }
    public string FileName { get; }
    public string Format { get; }
    public long SizeBytes { get; }
    public bool IsRefused { get; }
    public string? RefusalReason { get; }

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
