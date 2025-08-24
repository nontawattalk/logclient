# Windows Syslog Agent (`logclient`)

`logclient` is a lightweight Windows service written in C#/.NET that
subscribes to one or more Windows Event Logs and forwards every event
to a remote syslog server.  The agent supports multiple output
formats, including **RFC 3164** (traditional BSD syslog), **RFC 5424**
with structured data and a **custom** format driven by a simple
template language.  It can be configured entirely via a single
`appsettings.json` file placed in `C:\ProgramData\WinSyslogAgent` and
will resume from where it left off after a reboot using per-channel
bookmarks.

## Features

- **Windows Service** – runs in the background and starts
  automatically at boot.  Uses the Generic Host from
  `Microsoft.Extensions.Hosting`.
- **Real‑time event subscription** – leverages
  `EventLogWatcher` to receive Windows events as soon as they are
  written without polling.
- **Bookmarking** – stores a bookmark per log channel in
  `%ProgramData%\WinSyslogAgent\bookmarks` so that no events are lost
  or duplicated across restarts.
- **RFC 3164 and RFC 5424 output** – choose between the
  traditional BSD format (`<PRI>MMM dd HH:mm:ss host tag: message`) or
  the modern IETF format (`<PRI>1 TIMESTAMP HOST APP PROCID MSGID
  [STRUCTURED] MSG`).  Facility and severity are derived from the
  channel and Windows event level respectively.
- **Custom templates** – supply your own message template with
  tokens like `{timestamp:O}`, `{event_id}`, `{message}` and more
  (see below).  Each token may include a format string following a
  colon.
- **UDP and TCP transport** – send syslog datagrams over UDP or
  length‑prefixed messages over TCP (octet‑counted framing per
  RFC 6587).
- **Batching** – configurable batch size and interval to tune
  throughput vs. latency.
- **Per‑channel facilities** – map Windows logs to syslog facility
  numbers (e.g. `Security` → auth (10), `Application`/`System` →
  local0 (16)).

## Quick start

1. **Build and publish** the service.  You need the .NET 8 SDK
   installed on a Windows machine.

   ```powershell
   cd src/WinSyslogAgent
   dotnet publish -c Release -r win-x64 -o publish --self-contained false
   ```

2. **Install as a Windows Service** (requires Administrator).  Copy
   the published files and configuration to their final locations and
   register the service using `sc.exe`.

   ```powershell
   $svcName = "WinSyslogAgent"
   $installDir = "C:\\Program Files\\WinSyslogAgent"
   $configDir  = "$Env:ProgramData\\WinSyslogAgent"

   New-Item -ItemType Directory -Force -Path $installDir | Out-Null
   Copy-Item .\publish\* $installDir -Recurse

   New-Item -ItemType Directory -Force -Path $configDir | Out-Null
   Copy-Item .\appsettings.json "$configDir\appsettings.json" -Force

   sc.exe create $svcName binPath= "\"$installDir\\WinSyslogAgent.exe\"" start= auto
   sc.exe start  $svcName
   ```

3. **Configure** the agent by editing
   `%ProgramData%\\WinSyslogAgent\\appsettings.json` to point at your
   syslog server and choose the desired output format.  Changes will
   take effect the next time the service is started.

## Configuration

Configuration lives entirely in `appsettings.json`.  The top‑level
`agent` object contains the following keys:

| Key              | Description                                                         | Default |
|------------------|---------------------------------------------------------------------|---------|
| `mode`           | Output format: `rfc3164`, `rfc5424` or `custom`.                    | `rfc5424` |
| `customTemplate` | Template string used when `mode` is `custom`.                       | Built‑in format |
| `server` / `port`| Address and port of your syslog server.                             | `192.0.2.10` / `514` |
| `protocol`       | `udp` or `tcp`.                                                     | `udp` |
| `hostname`       | Override for the hostname in syslog messages. Empty → machine name. | empty |
| `appName`        | Application name shown in the syslog message.                       | `WinSyslogAgent` |
| `maxQueue`       | Maximum number of events to buffer before sending.                  | `10000` |
| `sendBatchSize`  | Number of events per network send.                                  | `50` |
| `sendIntervalMs` | Milliseconds between sends.                                         | `200` |
| `channels`       | Array of Event Log names to subscribe to.                           | `Application,System,Security` |
| `facilityMap`    | Map of log names to syslog facility numbers.                        | see sample |

### Template tokens

When `mode` is set to `custom`, `customTemplate` defines how the
message is constructed.  Each token is enclosed in braces and may
include a format string after a colon (e.g. `{timestamp:O}`).

Available tokens include:

- `timestamp` – the event timestamp; format string applies (e.g. `O` for ISO‑8601).
- `hostname` – host name used in the syslog message.
- `computer` – the computer name recorded in the event.
- `channel` – log name (e.g. `System`).
- `provider` – event provider name.
- `event_id` – Windows event identifier.
- `level` – human‑readable level (e.g. `Information`, `Error`).
- `opcode` – opcode name.
- `task` – task name.
- `keywords` – comma separated list of keywords.
- `user` – SID of the user that generated the event.
- `record_id` – event record identifier.
- `process_id` – process id.
- `thread_id` – thread id.
- `message` – the rendered event message.

Example:

```json
"customTemplate": "{timestamp:O} {computer} [{channel}] id={event_id} lvl={level} msg={message}"
```

## License

This project is distributed under the SRAN License 1.0.  See
[`LICENSE`](LICENSE) for full details.

## Contributing

Contributions are welcome!  Feel free to open issues or pull
requests.  Please ensure that any new functionality includes
appropriate documentation and unit tests where feasible.
