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
    private const string DefaultQueueName = "CASE.ATTACH.Q";
    private const string DefaultOutputRoot = "out";

    public string Host { get; init; } = DefaultHost;
    public string VpnName { get; init; } = DefaultVpnName;
    public string UserName { get; init; } = DefaultUserName;
    public string Password { get; init; } = DefaultPassword;
    public string QueueName { get; init; } = DefaultQueueName;
    public string OutputRoot { get; init; } = DefaultOutputRoot;

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
            QueueName = config["Demo:QueueName"] ?? DefaultQueueName,
            OutputRoot = config["Demo:OutputRoot"] ?? DefaultOutputRoot,
        };
    }
}
