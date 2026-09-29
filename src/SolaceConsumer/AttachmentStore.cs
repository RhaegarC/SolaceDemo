using System.IO;

namespace SolaceConsumer;

/// <summary>
/// Writes a received attachment to disk.
///
/// Delivery is at-least-once, so writing is deliberately a plain overwrite: a redelivered
/// message produces the same bytes at the same path and no duplicate file. A failure here
/// is transient by assumption — the caller retries, and settles the message either way.
/// </summary>
public sealed class AttachmentStore
{
    private const int WriteAttempts = 3;
    private const int RetryDelayMs = 150;

    public void Write(string path, byte[] payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.WriteAllBytes(path, payload);
                return;
            }
            catch (IOException) when (attempt < WriteAttempts)
            {
                Thread.Sleep(RetryDelayMs * attempt);
            }
        }
    }
}
