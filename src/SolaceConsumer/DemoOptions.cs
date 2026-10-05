using Microsoft.Extensions.Configuration;

namespace SolaceConsumer;

/// <summary>
/// Connection and demo settings, read from appsettings.json beside the executable.
///
/// SolaceClient carries the same broker values in its own copy. The queue name is only
/// here, because only the consumer binds to it; the publisher reaches it through the
/// topic subscription the bring-up script creates.
/// </summary>
public sealed class DemoOptions
{
    private const string DefaultHost = "tcp://localhost:55555";
    private const string DefaultVpnName = "default";
    private const string DefaultUserName = "appuser";
    private const string DefaultPassword = "apppass";
    private const string DefaultQueueName = "test/integration/dataTran/sysname/cc/case/attach/q";
    private const string DefaultOutputRoot = "out";

    public string Host { get; init; } = DefaultHost;
    public string VpnName { get; init; } = DefaultVpnName;
    public string UserName { get; init; } = DefaultUserName;
    public string Password { get; init; } = DefaultPassword;
    public string QueueName { get; init; } = DefaultQueueName;
    public string OutputRoot { get; init; } = DefaultOutputRoot;

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

        return new DemoOptions
        {
            Host = config["Solace:Host"] ?? DefaultHost,
            VpnName = config["Solace:VpnName"] ?? DefaultVpnName,
            UserName = config["Solace:UserName"] ?? DefaultUserName,
            Password = config["Solace:Password"] ?? DefaultPassword,
            QueueName = config["Demo:QueueName"] ?? DefaultQueueName,
            OutputRoot = config["Demo:OutputRoot"] ?? DefaultOutputRoot,
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
    /// hand rather than with GetValue&lt;T&gt; so the consumer needs no reference to the
    /// configuration binder package for one setting.
    /// </summary>
    private static bool ReadBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

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
