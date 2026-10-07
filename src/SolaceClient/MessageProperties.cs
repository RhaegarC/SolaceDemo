namespace SolaceClient;

/// <summary>
/// User property names carried on every attachment message.
///
/// SolaceConsumer declares the same names in its own copy of this file. There is no
/// shared assembly by design, so a rename here has to be mirrored there — the consumer
/// rejects a message whose properties it cannot find, naming the one that is missing,
/// which is what makes the drift visible rather than silent.
/// </summary>
public static class MessageProperties
{
    public const string CaseId = "case-id";

    /// <summary>The original filename. Authoritative — the topic carries a sanitized slug instead.</summary>
    public const string FileName = "filename";

    public const string ContentType = "content-type";
    public const string Size = "size";
    public const string Sha256 = "sha256";

    /// <summary>
    /// Present only on a chunked attachment, and the value the consumer uses to tell a part
    /// of a file from a whole one. It is carried by every chunk and by no whole file, so the
    /// consumer's test for it *is* the classification.
    /// </summary>
    public const string TransferId = "transfer-id";

    /// <summary>Zero-based position of this chunk within the transfer.</summary>
    public const string ChunkIndex = "chunk-index";

    /// <summary>Total chunks in the transfer. Always 2 or more, since one chunk is not a chunking.</summary>
    public const string ChunkCount = "chunk-count";

    /// <summary>Byte count of the whole original file, checked once the parts are back together.</summary>
    public const string FileSize = "file-size";

    /// <summary>
    /// SHA-256 of the whole original file. <see cref="Sha256"/> covers this message's bytes
    /// and so verifies each chunk as it lands; this one verifies the file after the merge,
    /// which is the only place a wrong or missing part can be caught.
    /// </summary>
    public const string FileSha256 = "file-sha256";
}
