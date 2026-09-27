# Racing League Tools UDP Dumper

Records F1 UDP telemetry (packet formats 2024, 2025, and 2026) into compressed `.dat` dumps.

Requires the .NET 9 SDK.

```
dotnet run -- --port 20777 --output ./dumps
```

Options: `-p` / `--port`, `-o` / `--output`, `-h` / `--help`.

Defaults are written to `udp_dumper_config.json` next to the executable: port 20777, 1024 MB per file, session timeout 120 seconds.

Publish for another OS:

```
dotnet publish -c Release -r linux-x64
```

Other RIDs: `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, `win-arm64`.
