# SolaceDemo — case attachments over Solace PubSub+

A case has a unique ID and owns several attachments of different formats, each under 10 MB.
`SolaceClient` (WPF) publishes them; `SolaceConsumer` (console) receives them and writes them to
disk.

The attachments travel **through** the broker as persistent messages — this is not a claim-check
demo, and the files are not copied out of band. The broker spools them, so a consumer that is
stopped while a case is published still receives every file when it comes back.

```
SolaceClient (WPF)                          SolaceConsumer (console)
  pick files, publish                         write to out/{caseId}/{fileName}
        │                                              ▲
        │ session.Send(Persistent)                    │ flow, client-ack
        ▼                                              │
  topic  case/{caseId}/attachment/{format}/{slug}      │
        │                                              │
        └────────► queue CASE.ATTACH.Q ◄───────────────┘
                    subscribed to case/>
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

payload    the file's bytes, verbatim

properties case-id       string   GUID
           filename      string   ORIGINAL name — authoritative for the disk path
           content-type  string   MIME type
           size          long     payload byte count
           sha256        string   hex digest, verified by the consumer
```

The filename appears twice on purpose. The **topic** carries a percent-encoded slug, because a topic
is UTF-8 and capped at 250 bytes and the filename is user-controlled. The **property** carries the
original, and it is the only value the consumer will use to name a file on disk.

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
3. **The cap.** Add a file of 11 MB. It is marked `REFUSED` in the list and nothing is published
   for it; the rest of the case publishes normally.

## Decisions and their costs

**Raw binary, not base64.** A JSON envelope would inflate the payload ~33%, cutting the usable file
ceiling from 10 MB to about 7.5 MB. Raw bytes keep the full budget.

**The cap defaults to 9,500,000 bytes, not 10,000,000.** The broker's limit applies to the whole
message, so a file *at* 10 MB plus its properties and headers would be refused by the broker after
passing client-side validation. `provision.sh` prints the broker's real `maxMsgSize`, and the client
prints the limit it read from the broker at connect — compare them if you change the cap.

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
- No dead message queue, no per-format routing, no case-completeness event.
