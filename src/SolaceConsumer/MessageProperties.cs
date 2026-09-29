namespace SolaceConsumer;

/// <summary>
/// User property names carried on every attachment message.
///
/// SolaceClient declares the same names in its own copy of this file. There is no shared
/// assembly by design, so if the publisher renames one of these the messages stop
/// carrying it — and <c>Require</c> in Program.cs rejects on the missing name, which is
/// what turns that drift into a visible rejection instead of a silent wrong write.
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
