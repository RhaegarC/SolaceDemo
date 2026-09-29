using Microsoft.Extensions.Configuration;

namespace SolaceClient;

/// <summary>
/// Connection and demo settings, read from appsettings.json beside the executable.
/// The consumer declares the same broker and queue values in its own appsettings.json;
/// those two files are the one thing here that can silently drift apart.
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

    public string Host { get; init; } = DefaultHost;
    public string VpnName { get; init; } = DefaultVpnName;
    public string UserName { get; init; } = DefaultUserName;
    public string Password { get; init; } = DefaultPassword;
    public string TopicPrefix { get; init; } = DefaultTopicPrefix;
    public long MaxAttachmentBytes { get; init; } = DefaultMaxAttachmentBytes;

    public static DemoOptions Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        return new DemoOptions
        {
            Host = config["Solace:Host"] ?? DefaultHost,
            VpnName = config["Solace:VpnName"] ?? DefaultVpnName,
            UserName = config["Solace:UserName"] ?? DefaultUserName,
            Password = config["Solace:Password"] ?? DefaultPassword,
            TopicPrefix = config["Demo:TopicPrefix"] ?? DefaultTopicPrefix,
            MaxAttachmentBytes = long.TryParse(config["Demo:MaxAttachmentBytes"], out var cap) && cap > 0
                ? cap
                : DefaultMaxAttachmentBytes,
        };
    }
}
