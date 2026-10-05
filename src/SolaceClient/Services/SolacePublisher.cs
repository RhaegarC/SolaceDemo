using SolaceSystems.Solclient.Messaging;

namespace SolaceClient.Services;

/// <summary>
/// Owns the Solace session used for publishing. Connect and Publish both block on the
/// broker, so callers run them off the UI thread.
/// </summary>
public sealed class SolacePublisher : IDisposable
{
    private readonly DemoOptions _options;
    private IContext? _context;
    private ISession? _session;
    private bool _disposed;

    public SolacePublisher(DemoOptions options) => _options = options;

    public bool IsConnected => _session is not null;

    /// <summary>
    /// The broker's own guaranteed message ceiling, reported at connect. This is the
    /// number the configured cap has to stay under.
    /// </summary>
    public long BrokerMaxGuaranteedMessageBytes { get; private set; }

    /// <summary>Raised for session lifecycle events, so the UI can show disconnects.</summary>
    public event Action<string>? StatusChanged;

    public void Connect()
    {
        ContextFactory.Instance.Init(new ContextFactoryProperties
        {
            SolClientLogLevel = SolLogLevel.Warning,
        });

        _context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);

        var sessionProperties = new SessionProperties
        {
            Host = _options.Host,
            VPNName = _options.VpnName,
            UserName = _options.UserName,
            Password = _options.Password,
            ReconnectRetries = 3,
            ConnectTimeoutInMsecs = 10_000,
        };

        var session = _context.CreateSession(sessionProperties, null, OnSessionEvent);

        var result = session.Connect();
        if (result != ReturnCode.SOLCLIENT_OK)
        {
            session.Dispose();
            throw new InvalidOperationException($"Connect to {_options.Host} failed: {result}");
        }

        _session = session;
        BrokerMaxGuaranteedMessageBytes = ReadMaxGuaranteedMessageBytes(session);
        StatusChanged?.Invoke($"Connected to {_options.Host}");
    }

    /// <summary>
    /// Publishes one attachment as a persistent message: the file's bytes are the payload,
    /// everything the consumer needs to route and name the file is a user property.
    /// Throws if the broker refuses the send, so the caller can report it per file.
    /// </summary>
    public void Publish(string topic, string caseId, string fileName, string contentType, byte[] payload)
    {
        var session = _session ?? throw new InvalidOperationException("Not connected.");

        using var message = session.CreateMessage();
        message.Destination = ContextFactory.Instance.CreateTopic(topic);
        message.DeliveryMode = MessageDeliveryMode.Persistent;
        message.BinaryAttachment = payload;
        message.HttpContentType = contentType;

        // The map has to be closed before the message is sent; leaving it open is a
        // native memory leak that only shows up under volume.
        var properties = message.CreateUserPropertyMap();
        properties.AddString(MessageProperties.CaseId, caseId);
        properties.AddString(MessageProperties.FileName, fileName);
        properties.AddString(MessageProperties.ContentType, contentType);
        properties.AddInt64(MessageProperties.Size, payload.LongLength);
        properties.AddString(MessageProperties.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant());
        properties.Close();

        var result = session.Send(message);
        if (result != ReturnCode.SOLCLIENT_OK)
        {
            throw new InvalidOperationException($"Publish to {topic} failed: {result}");
        }
    }

    private long ReadMaxGuaranteedMessageBytes(ISession session)
    {
        try
        {
            var capability = session.GetCapability(CapabilityType.MAX_GUARANTEED_MSG_SIZE);
            return Convert.ToInt64(capability.Value.Value);
        }
        catch
        {
            // Purely informational; the configured cap is what actually applies.
            return 0;
        }
    }

    private void OnSessionEvent(object? sender, SessionEventArgs args) =>
        StatusChanged?.Invoke($"{args.Event} {args.Info}".Trim());

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_session is not null)
        {
            try { _session.Disconnect(); } catch { /* shutting down anyway */ }
            _session.Dispose();
            _session = null;
        }

        _context?.Dispose();
        _context = null;

        ContextFactory.Instance.Cleanup();
    }
}
