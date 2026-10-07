using System.Security.Cryptography;
using SolaceSystems.Solclient.Messaging.SDT;

namespace SolaceConsumer;

/// <summary>
/// What an arriving chunk turned out to be: where it sits in the file, and the file itself
/// once the last part has landed.
/// </summary>
/// <param name="Index">Zero-based position of the chunk that just arrived.</param>
/// <param name="Count">How many chunks the whole file was split into.</param>
/// <param name="Assembled">The file's bytes once complete; null while chunks are still outstanding.</param>
public readonly record struct ChunkProgress(int Index, int Count, byte[]? Assembled);

/// <summary>
/// Collects the chunks of a split attachment until every one has arrived, then hands back the
/// file's bytes.
///
/// This is the only state the consumer keeps between messages. It is bounded on purpose: a
/// sender that stops part-way leaves pieces that will never complete, and nothing downstream
/// would ever remove them, because every message is acked on arrival and never redelivered.
/// </summary>
public sealed class ChunkAssembly
{
    /// <summary>
    /// The most chunks one file may be split into. The count is a number the sender chose, so
    /// it is checked before an array is sized from it — otherwise one malformed message could
    /// reserve the memory for a file that is never coming.
    /// </summary>
    public const int MaxChunks = 10_000;

    private const int MaxPendingTransfers = 32;
    private const long MaxBufferedBytes = 256L * 1024 * 1024;

    private readonly Dictionary<string, PendingFile> _pending = new(StringComparer.Ordinal);

    // Arrival order, so the oldest part-built transfer is the one abandoned when a bound is
    // reached. A dictionary has no order to evict by.
    private readonly List<string> _arrivalOrder = [];

    private readonly Action<string> _onAbandoned;

    private long _bufferedBytes;

    /// <param name="onAbandoned">
    /// Called when a part-built transfer is dropped to stay within the bounds. An abandoned
    /// transfer is a file that will never be written, so the caller is told rather than left
    /// to notice its absence.
    /// </param>
    public ChunkAssembly(Action<string> onAbandoned) => _onAbandoned = onAbandoned;

    /// <summary>
    /// Adds one chunk. Returns with <c>Assembled</c> null while parts are still outstanding,
    /// and with the whole file once the last one arrives — at which point the transfer's state
    /// is released, so a chunk arriving after completion starts a fresh, never-finishing
    /// transfer rather than corrupting the finished one.
    /// </summary>
    public ChunkProgress Add(IMapContainer? properties, string caseId, string fileName, byte[] payload)
    {
        var transferId = RequiredProperties.String(properties, MessageProperties.TransferId);
        var index = RequiredProperties.Int32(properties, MessageProperties.ChunkIndex);
        var count = RequiredProperties.Int32(properties, MessageProperties.ChunkCount);
        var fileSize = RequiredProperties.Int64(properties, MessageProperties.FileSize);
        var fileSha256 = RequiredProperties.String(properties, MessageProperties.FileSha256);

        if (count < 2 || count > MaxChunks)
        {
            throw new RejectedAttachmentException($"chunk-count {count} is outside 2..{MaxChunks}");
        }

        if (index < 0 || index >= count)
        {
            throw new RejectedAttachmentException($"chunk-index {index} is outside 0..{count - 1}");
        }

        if (fileSize <= 0)
        {
            throw new RejectedAttachmentException($"file-size {fileSize} is not positive");
        }

        if (!_pending.TryGetValue(transferId, out var pending))
        {
            MakeRoom(payload.LongLength);

            pending = new PendingFile(caseId, fileName, count, fileSize, fileSha256);
            _pending.Add(transferId, pending);
            _arrivalOrder.Add(transferId);
        }
        else if (pending.Count != count
                 || pending.FileSize != fileSize
                 || !string.Equals(pending.FileName, fileName, StringComparison.Ordinal)
                 || !string.Equals(pending.FileSha256, fileSha256, StringComparison.OrdinalIgnoreCase))
        {
            // Chunks that disagree about the file they belong to cannot be one transfer, and
            // believing the first version would merge two files into one attachment.
            Discard(transferId);
            throw new RejectedAttachmentException($"transfer-id {transferId} arrived with conflicting chunk metadata");
        }

        if (pending.Chunks[index] is null)
        {
            pending.Received++;
            _bufferedBytes += payload.LongLength;
        }

        // A chunk that arrives twice is not a second copy: overwriting keeps this as
        // idempotent as the whole-file write already is.
        pending.Chunks[index] = payload;

        if (pending.Received < pending.Count)
        {
            return new ChunkProgress(index, count, null);
        }

        var assembled = Assemble(pending);
        Discard(transferId);

        return new ChunkProgress(index, count, assembled);
    }

    /// <summary>
    /// Concatenates the parts in index order and checks the result against what the sender
    /// declared. This is the only point at which a wrong or missing part can be caught: the
    /// per-message hash proves each chunk arrived intact, but not that it was the right chunk.
    /// </summary>
    private static byte[] Assemble(PendingFile pending)
    {
        var total = BufferedBytes(pending);

        if (total != pending.FileSize)
        {
            throw new RejectedAttachmentException(
                $"reassembled {total:N0} bytes but the sender declared {pending.FileSize:N0}");
        }

        if (total > int.MaxValue)
        {
            // Unreachable for anything the publisher will send, but a byte array cannot be
            // larger than this and the cast below would wrap rather than fail.
            throw new RejectedAttachmentException($"reassembled file of {total:N0} bytes is too large for this process");
        }

        var file = new byte[(int)total];
        var offset = 0;
        foreach (var chunk in pending.Chunks)
        {
            Buffer.BlockCopy(chunk!, 0, file, offset, chunk!.Length);
            offset += chunk.Length;
        }

        var actual = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
        if (!string.Equals(actual, pending.FileSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new RejectedAttachmentException($"file-sha256 mismatch (expected {pending.FileSha256}, got {actual})");
        }

        return file;
    }

    /// <summary>
    /// Abandons oldest-first until both bounds are satisfied. The incoming transfer is not in
    /// the map yet, so it can never evict itself: a file larger than the whole budget still
    /// completes, at the cost of whatever was part-built before it.
    /// </summary>
    private void MakeRoom(long incomingBytes)
    {
        while (_pending.Count > 0
               && (_pending.Count >= MaxPendingTransfers || _bufferedBytes + incomingBytes > MaxBufferedBytes))
        {
            var oldest = _arrivalOrder[0];
            var pending = _pending[oldest];

            Discard(oldest);

            // Named, not silent: this is a file that will never be written, and the line is
            // the only trace it leaves.
            _onAbandoned(
                $"incomplete transfer {oldest} ({pending.CaseId}  {pending.FileName}) abandoned at {pending.Received}/{pending.Count} chunks");
        }
    }

    private void Discard(string transferId)
    {
        if (_pending.Remove(transferId, out var removed))
        {
            _bufferedBytes -= BufferedBytes(removed);
        }

        _arrivalOrder.Remove(transferId);
    }

    private static long BufferedBytes(PendingFile pending)
    {
        var total = 0L;
        foreach (var chunk in pending.Chunks)
        {
            total += chunk?.LongLength ?? 0;
        }

        return total;
    }

    private sealed class PendingFile
    {
        public PendingFile(string caseId, string fileName, int count, long fileSize, string fileSha256)
        {
            CaseId = caseId;
            FileName = fileName;
            Count = count;
            FileSize = fileSize;
            FileSha256 = fileSha256;
            Chunks = new byte[count][];
        }

        public string CaseId { get; }
        public string FileName { get; }
        public int Count { get; }
        public long FileSize { get; }
        public string FileSha256 { get; }

        /// <summary>One slot per index; null means that part has not arrived.</summary>
        public byte[][] Chunks { get; }

        public int Received { get; set; }
    }
}
