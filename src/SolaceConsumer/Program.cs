using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SolaceConsumer;
using SolaceSystems.Solclient.Messaging;
using SolaceSystems.Solclient.Messaging.SDT;

var options = DemoOptions.Load();
var store = new AttachmentStore();
var assembly = new ChunkAssembly(abandoned => Log(abandoned));

Log($"host {options.Host}  vpn {options.VpnName}  user {options.UserName}");
Log($"queue {options.QueueName}, subscribed to topic case/>");

// Loaded before the SDK is initialised, so an unreadable or wrong-password container is a
// printed line here rather than an opaque failure out of the native library.
X509Certificate2? clientCertificate = null;
if (options.UseClientCertificate)
{
    var certificatePath = options.ClientCertificateFile!;
    if (!File.Exists(certificatePath))
    {
        Log($"client certificate not found: {certificatePath}");
        Log("  the path is resolved against the application directory, beside appsettings.json");
        return 1;
    }

    try
    {
        // Exportable is not optional: the SDK marshals the private key out of this object
        // into the native TLS stack, and a key loaded into a non-exportable container
        // fails with "Failed to load certificate private key bytes".
        clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath, options.ClientCertificatePassword, X509KeyStorageFlags.Exportable);
    }
    catch (CryptographicException ex)
    {
        Log($"could not load {Path.GetFileName(certificatePath)}: {ex.Message}");
        Log("  Solace:PrivateKeyPassword must be the password of the PKCS#12 container");
        return 1;
    }

    Log($"client certificate {Path.GetFileName(certificatePath)} → auth scheme CLIENT_CERTIFICATE");
    Log($"  subject {clientCertificate.Subject}, expires {clientCertificate.NotAfter:yyyy-MM-dd}");
}

if (!options.ValidateServerCertificate)
{
    // Loud, because this is the check that the process is talking to the broker it thinks
    // it is. With it off, anything holding a valid certificate for the hostname is accepted.
    Log("WARNING: broker certificate validation is disabled — the broker is not authenticated");
}

// Initialise and dispose explicitly rather than with using: the SDK's cleanup has to
// run after the session is gone, and using would reverse that order.
ContextFactory.Instance.Init(new ContextFactoryProperties
{
    SolClientLogLevel = SolLogLevel.Warning,
});
var context = ContextFactory.Instance.CreateContext(new ContextProperties(), null);

var sessionProperties = new SessionProperties
{
    Host = options.Host,
    VPNName = options.VpnName,
    UserName = options.UserName,
    Password = options.Password,
    ReconnectRetries = 3,
    ConnectTimeoutInMsecs = 10_000,
};

if (clientCertificate is not null)
{
    // The certificate is what proves who this client is; the password above is the
    // basic-auth credential and is not consulted under this scheme.
    //
    // The certificate is handed over as an X509Certificate2 rather than as a file name.
    // The SDK's SSLClientCertificateFile/SSLClientPrivateKeyFile properties are read by
    // an OpenSSL-based native layer that expects PEM: pointed at a PKCS#12 container it
    // fails with "no start line", because a PKCS#12 has no such header.
    sessionProperties.AuthenticationScheme = AuthenticationSchemes.CLIENT_CERTIFICATE;
    sessionProperties.SSLClientCertificate = clientCertificate;
}

if (!options.ValidateServerCertificate)
{
    sessionProperties.SSLValidateCertificate = false;
}
else if (options.TrustedCaDirectory is { } trustedCaDirectory)
{
    // For a broker behind a private CA, rather than installing that CA machine-wide.
    sessionProperties.SSLTrustStoreDir = trustedCaDirectory;
}

ISession session;
try
{
    session = context.CreateSession(sessionProperties, null, OnSessionEvent);

    var connectResult = session.Connect();
    if (connectResult != ReturnCode.SOLCLIENT_OK)
    {
        Log($"connect failed: {connectResult}");
        session.Dispose();
        context.Dispose();
        ContextFactory.Instance.Cleanup();
        return 1;
    }
}
catch (OperationErrorException ex)
{
    // The SDK puts the useful part in ErrorInfo; ex.Message is only ever "Failed to
    // create session" or "Failed to connect session".
    Log($"could not establish the session: {ex.ErrorInfo}");

    if (options.ValidateServerCertificate && options.TrustedCaDirectory is null)
    {
        // Validation on with no trust store is an error to this SDK, not a fallback to
        // the operating system's store. Both settings are in appsettings.json.
        Log("  validation is on but no trust store is configured — set Solace:TrustedCaDirectory");
        Log("  to a directory of CA certificates, or Solace:ValidateServerCertificate to false");
    }

    context.Dispose();
    ContextFactory.Instance.Cleanup();
    return 1;
}

Log($"connected, writing under {Path.GetFullPath(options.OutputRoot)}");

var queue = ContextFactory.Instance.CreateQueue(options.QueueName);
var flowProperties = new FlowProperties
{
    AckMode = MessageAckMode.ClientAck,
    // One unacknowledged message at a time keeps the log in step with the writes, and
    // means a message is only outstanding while it is actually being processed.
    MaxUnackedMessages = 1,
};

IFlow? flow = null;
flow = session.CreateFlow(flowProperties, queue, null, (_, args) => OnMessage(flow!, args, options, store, assembly), OnFlowEvent);

flow.Start();
Log("waiting for attachments — press Enter to exit");

Console.ReadLine();

flow.Stop();
flow.Dispose();
session.Disconnect();
session.Dispose();
context.Dispose();
ContextFactory.Instance.Cleanup();
Log("stopped");
return 0;

static void OnMessage(IFlow flow, MessageEventArgs args, DemoOptions options, AttachmentStore store, ChunkAssembly assembly)
{
    var message = args.Message;

    try
    {
        var properties = message.UserPropertyMap;

        var caseId = RequiredProperties.String(properties, MessageProperties.CaseId);
        var fileName = RequiredProperties.String(properties, MessageProperties.FileName);
        var payload = message.BinaryAttachment ?? [];

        // This hash covers the bytes of this message, so it holds for a chunk exactly as it
        // does for a whole file and is checked before either path is taken. It proves the
        // chunk arrived intact; only the file hash after the merge can prove it was the
        // right chunk.
        VerifyHash(properties, payload);

        // A part of a file is told from a whole one by whether the transfer id is there at all.
        // It is the property a chunked file must carry and a whole one cannot, so its presence
        // is the classification and no separate flag is needed.
        if (RequiredProperties.OptionalString(properties, MessageProperties.TransferId) is null)
        {
            var path = LocalFileNames.ResolveUnder(options.OutputRoot, caseId, fileName);
            store.Write(path, payload);

            Log($"[ok]       {caseId}  {fileName}  {FormatSize(payload.LongLength)} → {path}");
            return;
        }

        var progress = assembly.Add(properties, caseId, fileName, payload);
        if (progress.Assembled is null)
        {
            Log($"[chunk]    {caseId}  {fileName}  {progress.Index + 1}/{progress.Count}");
            return;
        }

        // Written only once the last chunk is in, so an incomplete transfer leaves nothing on
        // disk rather than a truncated file that looks like a whole one.
        var assembledPath = LocalFileNames.ResolveUnder(options.OutputRoot, caseId, fileName);
        store.Write(assembledPath, progress.Assembled);

        Log($"[ok]       {caseId}  {fileName}  {FormatSize(progress.Assembled.LongLength)} from {progress.Count} chunks → {assembledPath}");
    }
    catch (RejectedAttachmentException ex)
    {
        Log($"[rejected] {ex.Message} → acked, not written");
    }
    catch (Exception ex)
    {
        // Anything else is treated as transient: AttachmentStore has already retried,
        // and the message is settled below rather than left to redeliver forever.
        Log($"[failed]   {ex.Message} → acked after retries");
    }
    finally
    {
        // Settle every message. On a durable queue an unsettled one is redelivered
        // indefinitely, so a single unacceptable message would stall everything
        // behind it — the one failure mode this demo must not have.
        try
        {
            flow.Ack(message.ADMessageId);
        }
        catch (Exception ex)
        {
            Log($"ack failed: {ex.Message}");
        }

        message.Dispose();
    }
}

static void VerifyHash(IMapContainer? properties, byte[] payload)
{
    // Required, not optional: a message that carries no digest is one that cannot be
    // checked, and slipping past the comparison is the opposite of what this is for.
    var expected = RequiredProperties.String(properties, MessageProperties.Sha256);

    var actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
    {
        throw new RejectedAttachmentException($"sha256 mismatch (expected {expected}, got {actual})");
    }
}

static void OnSessionEvent(object? sender, SessionEventArgs args) =>
    Log($"session: {args.Event} {args.Info}".TrimEnd());

static void OnFlowEvent(object? sender, FlowEventArgs args) =>
    Log($"flow:    {args.Event} {args.Info}".TrimEnd());

static string FormatSize(long bytes) =>
    bytes >= 1_048_576 ? $"{bytes / 1_048_576.0:0.0} MB" : $"{bytes / 1024.0:0.0} KB";

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {message}");
