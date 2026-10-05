using System.IO;
using System.Text;

namespace SolaceConsumer;

/// <summary>
/// Turns a message-supplied name into something safe to create on disk.
///
/// This is a different job from the publisher's topic sanitizer. That one only has to
/// survive the broker's 250-byte UTF-8 rule; this one is the boundary between a string
/// that arrived over the network and the filesystem. Both the filename and the case id
/// are treated as hostile: either could say <c>..\..\Windows\evil.dll</c>, name a
/// reserved Windows device, or carry a drive-absolute path.
///
/// Four things happen, in order, and the last one is the one that actually holds:
/// strip to the basename, replace anything invalid, refuse the reserved shapes, and
/// finally assert the fully-resolved path is still inside the case's own directory.
/// </summary>
public static class LocalFileNames
{
    private const int MaxNameLength = 100;

    // Windows resolves these as devices regardless of extension, so "CON.txt" is as
    // unusable as "CON".
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Sanitizes one path segment. <paramref name="what"/> names the field in the
    /// rejection message, so the log says which property was at fault.
    /// </summary>
    public static string Sanitize(string suppliedName, string what)
    {
        var name = Basename(suppliedName ?? string.Empty);

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(IsInvalid(ch) ? '_' : ch);
        }

        // Windows silently strips trailing dots and spaces, which would make the name
        // that lands on disk differ from the one we validated.
        name = builder.ToString().TrimEnd(' ', '.');

        if (name.Length == 0)
        {
            throw new RejectedAttachmentException($"{what} \"{suppliedName}\" leaves no usable name");
        }

        if (name is "." or "..")
        {
            throw new RejectedAttachmentException($"{what} \"{suppliedName}\" is a traversal name");
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedDeviceNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
        {
            throw new RejectedAttachmentException($"{what} \"{suppliedName}\" is a reserved Windows device name");
        }

        return CapLength(name);
    }

    /// <summary>
    /// Resolves the file to write for one message, under <c>{root}/{caseId}/{fileName}</c>.
    /// Throws <see cref="RejectedAttachmentException"/> if the result would fall outside
    /// the case's directory.
    /// </summary>
    public static string ResolveUnder(string rootDirectory, string caseId, string fileName)
    {
        var safeCaseId = Sanitize(caseId, "case-id");
        var safeFileName = Sanitize(fileName, "filename");

        // GetFullPath before comparing: it collapses any ".." that survived, so the
        // comparison is against the path the filesystem would actually use.
        var caseRoot = Path.GetFullPath(Path.Combine(rootDirectory, safeCaseId));
        var fullPath = Path.GetFullPath(Path.Combine(caseRoot, safeFileName));

        var prefix = caseRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new RejectedAttachmentException($"filename \"{fileName}\" escapes the case directory");
        }

        return fullPath;
    }

    /// <summary>
    /// Everything up to and including the last separator. Splits on both separators
    /// rather than using Path.GetFileName so the behaviour does not change with the
    /// host OS — the producer's platform is not ours to assume.
    /// </summary>
    private static string Basename(string value)
    {
        var cut = value.LastIndexOfAny(['/', '\\']);
        return cut >= 0 ? value[(cut + 1)..] : value;
    }

    private static bool IsInvalid(char c) =>
        c < ' ' || c == '\u007f' || "<>:\"/\\|?*".Contains(c);

    private static string CapLength(string name)
    {
        if (name.Length <= MaxNameLength) return name;

        var extension = Path.GetExtension(name);
        if (extension.Length >= MaxNameLength) extension = string.Empty;

        var room = MaxNameLength - extension.Length;
        var stem = Path.GetFileNameWithoutExtension(name);

        return string.Concat(stem.AsSpan(0, Math.Min(stem.Length, room)), extension);
    }
}
