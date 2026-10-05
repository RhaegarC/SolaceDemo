using System.IO;
using System.Text;

namespace SolaceClient;

/// <summary>
/// Builds the routing topic an attachment is published on:
/// <c>{prefix}/{caseId}/attachment/{format}/{fileNameSlug}</c>.
///
/// Topics are UTF-8 and the broker caps one at 250 bytes, so everything outside a
/// conservative ASCII set is percent-encoded and the filename level is truncated to
/// whatever room is left. Percent-encoding also removes the reserved shapes: a name
/// beginning with '!' encodes to "%21" and can no longer be read as the subscription
/// exception marker.
///
/// The topic is for routing and for reading in a log. The consumer takes the filename
/// from the message's filename property, never from here.
/// </summary>
public static class AttachmentTopics
{
    /// <summary>The broker's limit on a topic string, in bytes.</summary>
    public const int MaxTopicBytes = 250;

    private const string FallbackSlug = "file";
    private const string FallbackFormat = "bin";

    public static string Build(string topicPrefix, string caseId, string fileName)
    {
        var format = FormatFor(fileName);
        var head = $"{topicPrefix}/{Slug(caseId, int.MaxValue)}/attachment/{format}/";

        // The head is assembled from a prefix, a case id and a fixed literal; only the
        // filename absorbs whatever budget is left.
        var room = MaxTopicBytes - Encoding.UTF8.GetByteCount(head);
        var slug = room > 0 ? Slug(fileName, room) : string.Empty;

        return head + (slug.Length > 0 ? slug : FallbackSlug);
    }

    /// <summary>
    /// The file's extension as a lowercase topic level, or "bin" when there isn't a
    /// usable one. The extension comes from a file the user picked, so it is validated
    /// rather than passed through.
    /// </summary>
    public static string FormatFor(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrEmpty(extension)) return FallbackFormat;

        var format = extension.TrimStart('.').ToLowerInvariant();
        if (format.Length is 0 or > 10) return FallbackFormat;

        foreach (var c in format)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c)) return FallbackFormat;
        }

        return format;
    }

    /// <summary>
    /// Percent-encodes anything outside [A-Za-z0-9._-], stopping once <paramref name="maxBytes"/>
    /// UTF-8 bytes have been used. Iterates by rune so a character outside the BMP is
    /// encoded whole rather than split into two lone surrogates.
    /// </summary>
    private static string Slug(string value, int maxBytes)
    {
        var builder = new StringBuilder();
        var used = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            var encoded = IsSafe(rune.Value)
                ? rune.ToString()
                : Uri.EscapeDataString(rune.ToString());

            var size = Encoding.UTF8.GetByteCount(encoded);
            if (used + size > maxBytes) break;

            builder.Append(encoded);
            used += size;
        }

        return builder.ToString();
    }

    private static bool IsSafe(int runeValue) =>
        runeValue is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
        || runeValue is '.' or '_' or '-';
}
