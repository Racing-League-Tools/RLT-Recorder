# RLT UDP Client

A small standalone recorder for F1 telemetry. It listens on the game's UDP port,
writes Racing League Tools dump files, and does nothing else — no database, no
league configuration, no account.

The intended flow: a lobby member records the session, sends the `.dat` file to
the league manager, and the manager replays it in RLT via
**Database → Replay session from UDP dump…**. All driver matching, points and
season handling stay in RLT where they belong.

## Why it exists

RLT itself records dumps, but only for people who run RLT. This is for everyone
else in the lobby — including console players, who can point the game's telemetry
at any IP on the network and record on a phone, a laptop or a Raspberry Pi.

## Layout

| Project | What it is |
|---|---|
| `src/RacingLeagueTools.UdpDumper` | Upstream dumper source, from the RLT author. Packet protocols, filtering and session detection. |
| `src/RltUdpClient.Core` | Socket, file and lifecycle handling built on top of it. |
| `src/RltUdpClient.Cli` | `rlt-udp-record` — headless recorder for an always-on box. |
| `src/RltUdpClient.Desktop` | Avalonia window for everyone else. |

The upstream project is vendored rather than referenced in place, so our patches
live in one tree. The pristine copy is kept outside this repository for diffing
when the author ships an update.

Patches applied to the vendored copy so far, each marked with a `Patched:` comment:

- `OutputType` changed from `Exe` to `Library`, so its `runtimeconfig.json` and
  `deps.json` do not collide with our own executables when publishing.

## Dump format

Byte-compatible with the upstream dumper, verified by comparing inflated output
of both against the same packet stream:

```
file = raw DEFLATE stream (RFC 1951 — no gzip/zlib wrapper, no magic)
body = repeated [int32 little-endian packet length][raw UDP packet]
```

No timestamps are stored; RLT reconstructs timing from the packets themselves.

Verified end to end on a real F1 25 qualifying session: recorded by this client
on a cloud Linux box fed telemetry over the public internet, producing a 4.5 MB
dump that RLT imported correctly through **Replay session from UDP dump**.

## What `Core` does differently

- **Writes straight to disk.** Upstream buffers the whole session in memory,
  which is fine on a desktop but not on a phone or a Pi Zero. The trade-off is
  that the file exists while it is still being written, so it is kept under a
  `.partial` name and moved into place when the session closes. Until then it is
  not listed for download — a streaming compressor holds data back, so a dump
  fetched mid-session can be truncated or even zero bytes.
- **Terminates the deflate stream.** Upstream reads its buffer before closing the
  compressor, so the final block never lands. No data is lost, but the stream is
  technically unterminated.
- **Names files after the session they contain.** Upstream reads the session id
  after the handler has already advanced it, so a file closed by a session change
  gets the *next* session's id.
- **Closes a session when the game says it is over.** The final classification
  packet ends a session, so the file is finished seconds later instead of after
  two minutes of silence. A short grace period first, because the game keeps
  sending history and position updates on the results screen, and those belong
  in the dump too.
- **Raises the socket receive buffer** from the ~64 KB default, which holds only
  about 50 telemetry packets.

## Getting files off a headless box

The recorder serves a small read-only page on port 20780 listing the recorded
dumps, with a live view of what it is doing right now — on a machine with no
screen, that page is both the download link and the only status display.

If that port is taken the server does not start and says so, and recording
carries on regardless. It deliberately does **not** drift to another port by
default: a file server nobody can find is worse than one that admits it failed.
Set `http_port_fallback` if you want it to search anyway.

The `.local` address comes from the operating system's own mDNS responder
(avahi, Bonjour, or Windows 10+), not from us — reimplementing one would only
fight with the responder already running on the machine.

## Configuration

`config.json` beside the binary, or wherever `--config` points. It is created
with defaults on first run. Anything given on the command line wins over it.

| Key | Default | Meaning |
|---|---|---|
| `port` | 20777 | UDP port the game sends telemetry to |
| `output_directory` | `./dumps` | Where `.dat` files are written; a relative path is taken from the config file's folder |
| `session_timeout_seconds` | 120 | Silence that closes the current session |
| `final_classification_grace_seconds` | 8 | Wait after the game reports final classification |
| `receive_buffer_bytes` | 4194304 | Socket receive buffer |
| `http_enabled` | true | Serve files and status over HTTP |
| `http_port` | 20780 | Port for that server |
| `http_port_fallback` | false | Search upward when the port is taken |
| `mdns_enabled` | true | Show a `<host>.local` address in the banner |
| `mdns_name` | `""` | Override the host name in that address |
| `auto_start` | true | Window only: start recording as soon as it opens |

## Starting the window from another program

The desktop app accepts the upstream dumper's options, so the main RLT
application — or a shortcut — can start it the same way:

```
RltUdpClient.exe --port 21777 [--output <folder>]
```

Started with either option, it begins recording immediately and does not save
those values to `config.json`: they belong to that run, and a later manual start
still uses the configured port. Anything unrecognised is logged in the window
and ignored.

## Installing on a Raspberry Pi or other always-on box

`deploy/install.sh` puts the binary in `/opt`, the config in `/etc`, the dumps
in `/var/lib`, registers a systemd service that starts on boot, and — when avahi
is present — advertises the file server over mDNS. It picks the right binary for
the machine's architecture and leaves an existing config alone on upgrade.

```
sudo ./install.sh
```

## Building

Needs the .NET 10 SDK.

```
dotnet build
dotnet run --project src/RltUdpClient.Cli -- --port 20777 --output ./dumps
```

Every release package — Windows window, Linux window and CLI (the CLI with its
installer), macOS app bundles and CLI — into `dist/`, versioned from
`Directory.Build.props`:

```
python tools/build_dist.py
```

Publishing a single target by hand:

```
dotnet publish src/RltUdpClient.Cli -c Release -r linux-arm64 --self-contained
```

Verified RIDs so far: `win-x64`, `linux-x64`, `linux-arm64` — the two Linux
builds have been run on real machines, not just cross-compiled.
The window on Linux ships as a tarball with `libICE`/`libSM` from Debian 12
beside it: Avalonia's X11 backend loads them unconditionally at start-up, and
minimal systems (WSL's Ubuntu, for one) do not have them. It keeps its config in
`~/.config/rlt-recorder/` and recordings in `~/RLT Recorder/`. Run on x64 under
WSLg; arm64 has only been cross-compiled.

`osx-arm64` and `osx-x64` cross-compile and come out ad-hoc signed, but have
not been run on a Mac yet. Inside a macOS app bundle the config lives in
`~/Library/Application Support/RLT Recorder/` and recordings default to
`~/RLT Recorder/`, since nothing may be written into the bundle itself.

## Testing without the game

`tools/harness` holds a synthetic F1 2025 packet sender and a `.dat` reader, so
the whole recording path can be exercised on localhost without owning the game.

```
python tools/harness/f1_sender.py --paced
python tools/harness/read_dat.py ./dumps
```
