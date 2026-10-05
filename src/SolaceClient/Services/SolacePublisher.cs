using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    /// <summary>
    /// The subject of the certificate this session authenticated with, or null when it
    /// authenticated with a username and password. Surfaced so the identity actually in
    /// use is visible, rather than implied by whichever settings happen to be present.
    /// </summary>
    public string? ClientCertificateSubject { get; private set; }

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

        // Client-certificate handling mirrors the consumer's, so one container and one
        // shape of appsettings serve both halves of the demo.
        if (_options.UseClientCertificate)
        {
            var certificate = LoadClientCertificate(_options);
            ClientCertificateSubject = certificate.Subject;

            // The certificate is what proves who this client is; the password above is
            // the basic-auth credential and is not consulted under this scheme.
            //
            // It is handed over as an X509Certificate2 rather than as a file name. The
            // SDK's SSLClientCertificateFile/SSLClientPrivateKeyFile properties are read
            // by an OpenSSL-based native layer that expects PEM: pointed at a PKCS#12
            // container it fails with "no start line", because a PKCS#12 has no such
            // header.
            sessionProperties.AuthenticationScheme = AuthenticationSchemes.CLIENT_CERTIFICATE;
            sessionProperties.SSLClientCertificate = certificate;
        }

        if (!_options.ValidateServerCertificate)
        {
            sessionProperties.SSLValidateCertificate = false;
        }
        else if (_options.TrustedCaDirectory is { } trustedCaDirectory)
        {
            // For a broker behind a private CA, rather than installing that CA machine-wide.
            sessionProperties.SSLTrustStoreDir = trustedCaDirectory;
        }

        ISession session;
        try
        {
            session = _context.CreateSession(sessionProperties, null, OnSessionEvent);

            var result = session.Connect();
            if (result != ReturnCode.SOLCLIENT_OK)
            {
                session.Dispose();
                throw new InvalidOperationException($"Connect to {_options.Host} failed: {result}");
            }
        }
        catch (OperationErrorException ex)
        {
            // The SDK puts the useful part in ErrorInfo; ex.Message is only ever "Failed
            // to create session" or "Failed to connect session".
            var hint = _options.ValidateServerCertificate && _options.TrustedCaDirectory is null
                // Validation on with no trust store is an error to this SDK, not a
                // fallback to the operating system's store. Both settings are in
                // appsettings.json.
                ? " (validation is on but no trust store is configured — set Solace:TrustedCaDirectory, or Solace:ValidateServerCertificate to false)"
                : string.Empty;

            throw new InvalidOperationException($"Could not establish the session: {ex.ErrorInfo}{hint}", ex);
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

    /// <summary>
    /// Loads the PKCS#12 container named in settings. Connect runs on a background thread
    /// and the UI shows the message verbatim, so each failure names what to change rather
    /// than leaving an opaque native error.
    /// </summary>
    private static X509Certificate2 LoadClientCertificate(DemoOptions options)
    {
        var path = options.ClientCertificateFile!;

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Client certificate not found: {path} — Solace:privateKeyName resolves against the application directory");
        }

        try
        {
            // Exportable is not optional: the SDK marshals the private key out of this
            // object into the native TLS stack, and a key loaded into a non-exportable
            // container fails with "Failed to load certificate private key bytes".
            return X509CertificateLoader.LoadPkcs12FromFile(
                path, options.ClientCertificatePassword, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Could not load {Path.GetFileName(path)}: {ex.Message} — Solace:PrivateKeyPassword must be the password of the PKCS#12 container",
                ex);
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
