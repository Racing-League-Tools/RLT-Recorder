# RLT Recorder

Records F1 telemetry into dump files for [Racing League Tools](https://racingleaguetools.com).

You race, the recorder saves the session, you send the `.dat` file to your
league manager, and they import it in RLT with **Database → Replay session from
UDP dump**. Results, points and driver matching all happen in RLT. The recorder
needs no account, no league setup and no RLT installation.

It works for PC and console players alike. The game sends telemetry to any
machine on your network, so you can record on a laptop, a Mac or a Raspberry Pi
while playing on an Xbox or PlayStation.

## Download

Get the latest version from [Releases](../../releases).

| Your computer | File |
|---|---|
| Windows | `rlt-recorder-gui-windows-x64.zip` |
| Mac with Apple M1/M2/M3/M4 | `rlt-recorder-gui-macos-apple-silicon.zip` |
| Mac with Intel | `rlt-recorder-gui-macos-intel.zip` |
| Linux PC or laptop | `rlt-recorder-gui-linux-x64.tar.gz` |
| Raspberry Pi 4/5 (64-bit OS) | `rlt-recorder-gui-linux-arm64.tar.gz` |

`gui` is the normal app with a window. The `cli` files are the command-line
version for a machine that runs without a screen; see
[Always-on machine](#always-on-machine).

## First start

**Windows.** Unzip and run `RltUdpClient.exe`. Windows may show "Windows
protected your PC" because the app is not signed yet: click **More info**, then
**Run anyway**. If the firewall asks, allow access on private networks.

**macOS.** Unzip and open `RLT Recorder.app`. macOS will say it cannot verify
the developer: click **Done**, go to **System Settings → Privacy & Security**,
scroll down and click **Open Anyway**. Allow incoming connections if asked.

**Linux.** Unpack and run:

```
tar xzf rlt-recorder-gui-linux-arm64.tar.gz
./rlt-recorder/RltUdpClient
./rlt-recorder/add-to-menu.sh      # optional: adds it to the desktop menu
```

The recorder starts recording as soon as it opens.

## In the game

**Settings → Telemetry Settings**:

- UDP Telemetry: **On**
- UDP IP Address: the address shown in the recorder window
- UDP Port: **20777**
- UDP Format: **2025**

Race as usual. When the status says **Receiving telemetry**, it is recording.
Each session is saved as its own file when it ends.

## Where the files are

| | Recordings | Settings |
|---|---|---|
| Windows | `dumps` next to the app | `config.json` next to the app |
| macOS | `~/RLT Recorder` | `~/Library/Application Support/RLT Recorder` |
| Linux | `~/RLT Recorder` | `~/.config/rlt-recorder` |

The **Open** button in the window takes you there. Recordings can also be
downloaded from any device on the same network at `http://<recorder's IP>:20780`.

## Always-on machine

For a Raspberry Pi or a small server without a screen, use the `cli` version.
On Linux it installs as a service that starts on boot:

```
tar xzf rlt-recorder-cli-linux-arm64.tar.gz
cd rlt-recorder-cli-linux && sudo ./install.sh
```

Open `http://<machine's IP>:20780` in a browser to watch it and download
recordings.

## Running RLT on the same PC

RLT itself listens on port 20777, so the recorder cannot use that port while
RLT is running, and it will say so. Either close RLT, or forward RLT's
telemetry to the recorder on another port (RLT's UDP forwarding) and start the
recorder with that port:

```
RltUdpClient.exe --port 21777
```

## Something not working

- **Status stays at "Waiting for telemetry"**: check the IP and port in the
  game, and that the firewall allows UDP 20777. On a laptop, both devices must
  be on the same network.
- **"Port 20777 is already being used"**: RLT or another telemetry app is
  running. See [Running RLT on the same PC](#running-rlt-on-the-same-pc).

Report problems in [Issues](../../issues).

## For developers

Building, the dump format and how to test without the game:
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Licence

MIT, see [LICENSE](LICENSE). The code in `src/RacingLeagueTools.UdpDumper` is
by [vlad-men](https://github.com/vlad-men), the author of Racing League Tools, under
[its own licence](src/RacingLeagueTools.UdpDumper/LICENSE). The Racing League
Tools name and logo are used with permission and are not covered by either
licence. Bundled third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
