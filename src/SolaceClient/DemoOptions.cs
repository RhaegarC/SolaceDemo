using System.IO;
using Microsoft.Extensions.Configuration;

namespace SolaceClient;

/// <summary>
/// Connection and demo settings, read from appsettings.json beside the executable.
/// The consumer declares the same broker values in its own appsettings.json; those two
/// files are the one thing here that can silently drift apart.
///
/// The client-certificate settings mirror the consumer's exactly, so one container and
/// one shape of configuration serve both halves of the demo.
/// </summary>
public sealed class DemoOptions
{
    private const string DefaultHost = "tcp://localhost:55555";
    private const string DefaultVpnName = "default";
    private const string DefaultUserName = "appuser";
    private const string DefaultPassword = "apppass";
    private const string DefaultTopicPrefix = "case";

    /// <summary>
    /// Sits below the broker's guaranteed ceiling rather than exactly on it: the limit
    /// applies to the whole message, and a file at the limit plus its properties and
    /// headers would be refused by the broker after passing our own check.
    /// </summary>
    private const long DefaultMaxAttachmentBytes = 9_500_000L;

    /// <summary>
    /// The size a file has to exceed to be split, and the size of each piece it is split
    /// into. One value rather than two: a chunk larger than the split point contradicts
    /// itself, and a smaller one sends more messages than the file needs.
    /// </summary>
    private const int DefaultChunkSizeBytes = 5_000_000;

    /// <summary>
    /// The largest whole file the client will send. Chunking lifts the per-message ceiling
    /// off an individual file; it does not remove the need for some ceiling, because the
    /// file is read into memory whole at both ends.
    /// </summary>
    private const long DefaultMaxFileBytes = 200_000_000L;

    public string Host { get; init; } = DefaultHost;
    public string VpnName { get; init; } = DefaultVpnName;
    public string UserName { get; init; } = DefaultUserName;
    public string Password { get; init; } = DefaultPassword;
    public string TopicPrefix { get; init; } = DefaultTopicPrefix;

    /// <summary>The ceiling on one message. Chunking keeps every chunk under it.</summary>
    public long MaxAttachmentBytes { get; init; } = DefaultMaxAttachmentBytes;

    /// <summary>A file larger than this is sent as several messages of at most this many bytes.</summary>
    public int ChunkSizeBytes { get; init; } = DefaultChunkSizeBytes;

    /// <summary>The ceiling on one whole file. A file at or over it is refused, not chunked.</summary>
    public long MaxFileBytes { get; init; } = DefaultMaxFileBytes;

    /// <summary>
    /// Full path to the PKCS#12 container holding the client certificate and its private
    /// key, or null to authenticate with a username and password instead.
    ///
    /// The SDK calls this the "private key file", but for PKCS#12 the one file is both the
    /// certificate and the key, and both SessionProperties are pointed at it.
    /// </summary>
    public string? ClientCertificateFile { get; init; }

    /// <summary>Password for the PKCS#12 container. Null when there is no client certificate.</summary>
    public string? ClientCertificatePassword { get; init; }

    /// <summary>
    /// Whether the broker's own certificate is validated against the trust store. True by
    /// default: turning this off removes the guarantee that the process is talking to the
    /// broker it thinks it is, so it is an explicit opt-out rather than a default.
    /// </summary>
    public bool ValidateServerCertificate { get; init; } = true;

    /// <summary>
    /// Directory of trusted CA certificates, for a broker behind a private CA that is not
    /// in the operating system's trust store. Null uses the system trust store.
    /// </summary>
    public string? TrustedCaDirectory { get; init; }

    /// <summary>True when a client certificate was supplied, which selects the auth scheme.</summary>
    public bool UseClientCertificate => ClientCertificateFile is { Length: > 0 };

    public static DemoOptions Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        // IConfiguration treats keys case-insensitively, so "privateKeyName" in the file
        // matches either spelling here.
        var certificateFile = ResolvePath(
            config["Solace:ClientCertificateFile"] ?? config["Solace:PrivateKeyName"]);

        var maxAttachmentBytes = ReadPositive(config["Demo:MaxAttachmentBytes"], DefaultMaxAttachmentBytes);

        // A chunk larger than one message can never be sent, so the message cap wins over
        // whatever chunk size was configured. Clamping silently beats discovering the
        // contradiction as a broker rejection at publish time.
        var chunkSizeBytes = (int)Math.Min(
            ReadPositive(config["Demo:ChunkSizeBytes"], DefaultChunkSizeBytes),
            Math.Min(maxAttachmentBytes, int.MaxValue));

        return new DemoOptions
        {
            Host = config["Solace:Host"] ?? DefaultHost,
            VpnName = config["Solace:VpnName"] ?? DefaultVpnName,
            UserName = config["Solace:UserName"] ?? DefaultUserName,
            Password = config["Solace:Password"] ?? DefaultPassword,
            TopicPrefix = config["Demo:TopicPrefix"] ?? DefaultTopicPrefix,
            MaxAttachmentBytes = maxAttachmentBytes,
            ChunkSizeBytes = chunkSizeBytes,
            MaxFileBytes = ReadPositive(config["Demo:MaxFileBytes"], DefaultMaxFileBytes),
            ClientCertificateFile = certificateFile,
            ClientCertificatePassword = config["Solace:ClientCertificatePassword"]
                ?? config["Solace:PrivateKeyPassword"]
                ?? config["Solace:Password"],
            ValidateServerCertificate = ReadBool(config["Solace:ValidateServerCertificate"], true),
            TrustedCaDirectory = ResolvePath(config["Solace:TrustedCaDirectory"]),
        };
    }

    /// <summary>
    /// Parses a boolean setting, falling back when it is absent or unparseable. Read by
    /// hand rather than with GetValue&lt;T&gt; so the client needs no reference to the
    /// configuration binder package for one setting.
    /// </summary>
    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Parses a byte-count setting, falling back when it is absent or unparseable. Zero and
    /// negative are treated as absent rather than honoured: a size of nothing is a typo in a
    /// configuration file, not a request to disable the feature it configures.
    /// </summary>
    private static long ReadPositive(string? value, long fallback) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;

    /// <summary>
    /// Resolves a configured file name against the application directory, which is where
    /// appsettings.json itself is read from. A relative path left alone would resolve
    /// against the working directory instead, and the two differ under "dotnet run".
    /// </summary>
    private static string? ResolvePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : Path.IsPathRooted(value) ? value
        : Path.Combine(AppContext.BaseDirectory, value);
}
