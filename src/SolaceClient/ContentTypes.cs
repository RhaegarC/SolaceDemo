using System.IO;

namespace SolaceClient;

/// <summary>
/// Maps a file extension to a MIME type for the content-type property. Anything not
/// listed is sent as application/octet-stream, which is what the consumer would do with
/// it anyway.
/// </summary>
public static class ContentTypes
{
    public const string Fallback = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".xml"] = "application/xml",
        [".json"] = "application/json",
        [".html"] = "text/html",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip",
        [".msg"] = "application/vnd.ms-outlook",
    };

    public static string For(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var contentType) ? contentType : Fallback;
}
