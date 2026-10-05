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
}
