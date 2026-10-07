# SolaceDemo — case attachments over Solace PubSub+

A case has a unique ID and owns several attachments of different formats. `SolaceClient` (WPF)
publishes them; `SolaceConsumer` (console) receives them and writes them to disk.

The attachments travel **through** the broker as persistent messages — this is not a claim-check
demo, and the files are not copied out of band. The broker spools them, so a consumer that is
stopped while a case is published still receives every file when it comes back.

A file that fits in one message is sent as one. A larger file is split over several messages, each
carrying its position in the file, and the consumer writes the file only once the last part has
arrived — so a file is no longer bounded by the broker's per-message limit.

```
SolaceClient (WPF)                          SolaceConsumer (console)
  pick files, publish                         write to out/{caseId}/{fileName}
        │                                              ▲
        │ session.Send(Persistent)                    │ flow, client-ack
        ▼                                              │
  topic  case/{caseId}/attachment/{format}/{slug}      │
        │                                              │
        └───► test/integration/dataTran/sysname/cc/case/attach/q
              queue, subscribed to case/  ◄────────────┘
```

## Project layout

| Path | What |
|---|---|
| [src/SolaceClient/](src/SolaceClient/) | WPF publisher — generates a case ID, picks files, publishes |
| [src/SolaceConsumer/](src/SolaceConsumer/) | Console consumer — binds the queue, writes attachments |
| [provision.sh](provision.sh) | Creates the queue, subscription, ACL profile and client user |
| [demo-up.sh](demo-up.sh) | Waits for the broker, then runs `provision.sh` |
| [docs/note.md](docs/note.md) | The broker container command |

## Message contract

```
topic      case/{caseId}/attachment/{format}/{sanitizedFileName}

payload    the file's bytes, verbatim — or one chunk of them, when the file is split

properties case-id       string   GUID
           filename      string   ORIGINAL name — authoritative for the disk path
           content-type  string   MIME type
           size          long     payload byte count
           sha256        string   hex digest of the payload, verified by the consumer

           and, on a chunk of a split file only:

           transfer-id   string   one id per file, shared by every chunk of it
           chunk-index   int      0-based position of this payload
           chunk-count   int      total chunks, always 2 or more
           file-size     long     byte count of the whole original file
           file-sha256   string   hex digest of the whole original file
```

The filename appears twice on purpose. The **topic** carries a percent-encoded slug, because a topic
is UTF-8 and capped at 250 bytes and the filename is user-controlled. The **property** carries the
original, and it is the only value the consumer will use to name a file on disk.

`size` and `sha256` describe the message, so they mean the same thing whether it carries a whole file
or one chunk of one. `file-size` and `file-sha256` describe the file the chunks belong to, and are the
only values that can be checked after the merge — the per-message hash proves a chunk arrived intact,
not that it was the right chunk. Chunks share the topic and the naming properties, so any single
message tells the consumer everything it needs to place it.

A message with no `transfer-id` is a whole file. That is the marker rather than `chunk-count` because
it is the property a chunked file must carry and a whole one cannot: its presence *is* the
classification, so no separate flag has to be kept in step with it. Absence is not inferred from a
default value — the SDK raises `FieldNotFoundException` for a key that was never set, and the reader
turns that into "not a chunk". A key that is present but not a string is rejected rather than read as
absent, so a malformed message cannot slip through as a whole file.

## Prerequisites

- .NET SDK 10
- A Solace PubSub+ broker reachable on SMF 55555 with SEMP on 8080, with admin credentials
- `curl` and `bash` (Git Bash is fine on Windows)

## Broker

The demo expects a broker to already be running — it does not start one. For a Linux host:

```bash
docker run -d -p 8080:8080 -p 55555:55555 --shm-size=1g \
  -v /var/lib/solace:/var/lib/solace \
  --env username_admin_globalaccesslevel=admin \
  --env username_admin_password=<password> \
  --name=solace solace/solace-pubsub-standard
```

A first boot takes a minute or two before SEMP answers.

## Run it

```bash
./demo-up.sh                      # wait for the broker, then provision it
```

Point it somewhere else with environment variables — the broker host, the ports, the VPN, the admin
credentials and the queue name are all overridable:

```bash
SOLACE_SMF_HOST=solace.internal SOLACE_ADMIN_PASSWORD=secret ./demo-up.sh
```

To match, set `Solace:Host` in **both** `appsettings.json` files to the same host. Those two files
are the one thing here that can drift apart, and they must agree on the broker.

Then, in two terminals:

```bash
dotnet run --project src/SolaceConsumer     # binds the queue and waits
```

```bash
dotnet run --project src/SolaceClient       # or F5 in Visual Studio
```

In the client: it shows a generated case ID → **Add files…** → select several files → **Publish case**.
Received files land under `out/{caseId}/{filename}` relative to the consumer's working directory.

### What to demonstrate

1. **The pipeline.** Publish a case; the consumer logs one `[ok]` line per file and the files appear
   on disk. The log shows the topic each file was published to.
2. **The spool.** Stop the consumer, publish another case, restart the consumer. Every file still
   arrives, because the queue held them while nothing was bound to it.
3. **Chunking.** Add a file larger than 5 MB. The list shows how many chunks it will be sent as, the
   log shows one `[chunk] n/N` line per part, and the consumer reports a single `[ok]` line once the
   file is complete. The file on disk matches the original byte for byte.
4. **The ceiling.** Add a file at or over 200 MB. It is marked `REFUSED` in the list and nothing is
   published for it; the rest of the case publishes normally.

## Client certificate authentication

Both apps can authenticate with a client certificate instead of a password. The settings and the code
path are the same on each side, so one container serves the publisher and the consumer. Put the
PKCS#12 container beside each `appsettings.json` — that is, in `src/SolaceClient/` and in
`src/SolaceConsumer/` — and name it:

```json
"Solace": {
  "Host": "tcps://broker-host:55443",
  "UserName": "costestclient",
  "privateKeyName": "costest.pfx",
  "PrivateKeyPassword": "<password of the .pfx>"
}
```

`PrivateKeyPassword` falls back to `Password` when it is absent, which is convenient only when the
two genuinely are the same value. Setting it explicitly is clearer.

`UserName` has to name the identity the certificate resolves to. A broker configured with
`authenticationClientCertUsernameSource: common-name` matches the certificate's *common name* — not
the file name, and not whatever else was typed here. A certificate whose CN is `test-client` needs
`UserName` to be `test-client`.

`*.pfx` is gitignored, so the container is never committed — it has to be copied into each project
by hand. The build copies it beside the executable in both, because the path is resolved against the
application directory, not the working directory.

Three things have to hold for this to connect, and each fails differently:

- **The broker must be listening for TLS.** Client certificate authentication is only offered over
  a TLS session, so the host has to be `tcps://` and the port has to be the broker's TLS port — not
  the plaintext SMF port. Pointing `tcps://` at a plaintext port gives
  `SSL error: 'wrong version number'`, which is the TLS client receiving a non-TLS reply.
- **The certificate must be signed with SHA-256 or better.** The SDK's native TLS stack is OpenSSL 3,
  which enforces a minimum security level and refuses a SHA-1 signature with
  `SSL_use_cert_and_key: 'ca md too weak'`. A SHA-1 certificate cannot be used at all, whatever the
  broker is configured to trust.
- **The broker's own certificate must be trusted, or validation turned off.** Set
  `"ValidateServerCertificate": false` for a broker behind a private CA, accepting that the broker
  is then not authenticated. Alternatively set `"TrustedCaDirectory"` to a directory of CA
  certificates. This SDK treats validation with *no* trust store as an error
  (`FailedLoadingTruststore`) rather than falling back to the operating system's store, so the two
  settings go together.

The certificate is handed to the SDK as an `X509Certificate2` rather than as a file name. The SDK's
`SSLClientCertificateFile` / `SSLClientPrivateKeyFile` properties are read by an OpenSSL-based native
layer that expects PEM, so pointing them at a PKCS#12 container fails with `'no start line'`. It is
also loaded with `X509KeyStorageFlags.Exportable`, because the SDK marshals the private key out to
that native layer and a non-exportable key fails with
`Failed to load certificate private key bytes`.

## Decisions and their costs

**Raw binary, not base64.** A JSON envelope would inflate the payload ~33%, cutting the usable file
ceiling from 10 MB to about 7.5 MB. Raw bytes keep the full budget.

**The per-message cap defaults to 9,500,000 bytes, not 10,000,000.** The broker's limit applies to
the whole message, so a payload *at* 10 MB plus its properties and headers would be refused by the
broker after passing client-side validation. Since chunking, nothing a user selects meets this cap
any more — it survives as the bound a chunk size is clamped to, so a misconfigured chunk size cannot
produce a message the broker will reject. The client asks the broker for its real ceiling at connect
(`MAX_GUARANTEED_MSG_SIZE`) and shows it, so the two can be compared. That number is not in the
message-VPN's SEMP config, so `provision.sh` can only report it where a broker exposes a `maxMsgSize`
field — on the broker used here it does not, and the script says so rather than inventing a figure.

**Split above 5 MB, and merged before writing.** `Demo:ChunkSizeBytes` is both the size at which a
file starts being split and the size of each piece — one number, because a chunk larger than the
split point contradicts itself. Chunks carry a shared `transfer-id` and an index, so they reassemble
in order no matter how they interleave with other files in flight. The consumer writes nothing until
the last chunk is in, so an interrupted transfer leaves no file on disk rather than one that is
silently short.

A whole file is still read into memory at both ends, which is why `Demo:MaxFileBytes` exists at all:
chunking lifts the per-message ceiling off a file but not the need for some ceiling. A file over it
is refused with its size and the limit, which is the job the per-message cap used to do.

**Guaranteed delivery, so every message is settled.** The consumer acks every message, including the
ones it rejects. On a durable queue an unsettled message is redelivered forever, so a single
unacceptable message would stall everything behind it. A rejection is logged and acked; a transient
write failure is retried three times, then logged and acked.

**Two projects, no test project.** By decision. The sanitizers are pure static methods
(`AttachmentTopics`, `LocalFileNames`) so they stay checkable by hand, but nothing asserts them
automatically — see the verification list below for what was actually run and observed.

**Two sanitizers, doing different jobs.** `AttachmentTopics` percent-encodes for the broker's topic
rules; `LocalFileNames` is the boundary between a string that arrived over the network and the
filesystem. The latter strips to the basename, replaces invalid characters, refuses Windows reserved
device names, caps length, and then asserts the resolved path is still inside the case's directory.
The case ID is sanitized too — it is a message property and is not trusted either.

## Limitations

- Filenames are not unique within a case in any enforced way; two attachments of the same name in one
  case overwrite, with no hash comparison. A redelivery is therefore idempotent, but two genuinely
  different files sharing a name silently keep only one.
- The consumer writes files inline on the SDK callback thread, with `MaxUnackedMessages = 1`. Fine at
  demo volume; a real consumer would hand off to a worker.
- The publisher does not retry a failed send; the file is marked `failed` in the list and the rest of
  the case continues.
- A chunked transfer that never completes is held in memory until the consumer is restarted, or until
  enough other transfers arrive to displace it: at most 32 part-built transfers and 256 MB in total,
  oldest abandoned first, with the abandoned one named in the log. Nothing retries a missing chunk,
  because every message is acked on arrival and never redelivered.
- No dead message queue, no per-format routing, no case-completeness event.
