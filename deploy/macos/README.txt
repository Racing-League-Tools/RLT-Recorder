RLT Recorder for macOS — TEST BUILD @VERSION@
=========================================

Not signed by Apple and not tested on a Mac yet. macOS will warn about it the
first time; that is expected.

Which file
----------
Apple menu > About This Mac > "Chip":
  Apple M1/M2/M3/M4  ->  rlt-recorder-gui-macos-apple-silicon.zip
  Intel              ->  rlt-recorder-gui-macos-intel.zip

The rlt-recorder-cli-*.tar.gz files are the command-line version, only needed
for an always-on machine without a screen.

First start
-----------
1. Unzip, move "RLT Recorder.app" wherever you like (e.g. Applications).
2. Double-click it. macOS says it cannot verify the developer. Click Done.
3. System Settings > Privacy & Security > scroll down > "Open Anyway" next to
   RLT Recorder, confirm with your password.
4. If asked whether to accept incoming network connections: Allow.
   Without that the game's telemetry never reaches the recorder.

If macOS says the app "is damaged and can't be opened", run this once in
Terminal instead of step 3, then open the app normally:

    xattr -cr "/Applications/RLT Recorder.app"

Where things are
----------------
Recordings:  ~/RLT Recorder/           (your home folder)
Settings:    ~/Library/Application Support/RLT Recorder/config.json

In the game
-----------
Telemetry settings > UDP Telemetry: On, UDP IP Address: this Mac's IP
(shown in the app), port 20777, format 2025.

What to report back
-------------------
- Did it open? Which macOS version and chip?
- Screenshot of the window.
- Does the status turn to "Receiving telemetry" during a session, and does a
  .dat file appear in ~/RLT Recorder afterwards?
- Does http://localhost:20780 open in a browser?
