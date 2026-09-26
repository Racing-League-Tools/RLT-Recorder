"""Builds every release package into dist/.

    python tools/build_dist.py

Windows window, Linux CLI (x64, arm64) with its installer, and macOS app
bundles plus CLI (Apple Silicon, Intel). Existing config.json and dumps/ in
dist are left alone, so a test install there survives a rebuild.

The version comes from Directory.Build.props, the one place it is set.
"""

import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
DESKTOP = ROOT / "src" / "RltUdpClient.Desktop"
CLI = ROOT / "src" / "RltUdpClient.Cli"

# Mach-O magics as they appear on disk: thin 64-bit, and a fat (universal) file.
MACHO_THIN = b"\xcf\xfa\xed\xfe"
MACHO_FAT = b"\xca\xfe\xba\xbe"
LC_CODE_SIGNATURE = 0x1D


def version() -> str:
    props = (ROOT / "Directory.Build.props").read_text(encoding="utf-8")
    return re.search(r"<Version>([^<]+)</Version>", props).group(1)


def publish(project: Path, rid: str, out: Path, single_file: bool = False) -> None:
    args = ["dotnet", "publish", str(project), "-c", "Release", "-r", rid,
            "--self-contained", "-o", str(out), "-nologo", "-v", "q"]
    if single_file:
        args.append("-p:PublishSingleFile=true")
    print(f"  publish {project.name} {rid}")
    subprocess.run(args, check=True)


def copy_binaries(src: Path, dst: Path) -> None:
    dst.mkdir(parents=True, exist_ok=True)
    for f in src.iterdir():
        if f.is_file() and f.suffix != ".pdb":
            shutil.copy2(f, dst / f.name)


def build_windows(work: Path) -> None:
    out = work / "win-x64"
    publish(DESKTOP, "win-x64", out, single_file=True)
    copy_binaries(out, DIST / "rlt-recorder-gui-windows")


def build_linux(work: Path) -> None:
    target = DIST / "rlt-recorder-cli-linux"
    target.mkdir(parents=True, exist_ok=True)
    for rid in ("linux-x64", "linux-arm64"):
        out = work / rid
        publish(CLI, rid, out)
        shutil.copy2(out / "rlt-udp-record", target / f"rlt-udp-record-{rid}")
    for f in (ROOT / "deploy").iterdir():
        if f.is_file():
            shutil.copy2(f, target / f.name)


def is_signed_macho(data: bytes) -> bool:
    """True for a thin Mach-O carrying a code signature. Apple Silicon refuses
    to run code with none at all, and a cross-compile would not warn about it."""
    ncmds = struct.unpack_from("<I", data, 16)[0]
    offset = 32
    for _ in range(ncmds):
        cmd, size = struct.unpack_from("<II", data, offset)
        if cmd == LC_CODE_SIGNATURE:
            return True
        offset += size
    return False


def zip_add(z: zipfile.ZipFile, name: str, data: bytes, mode: int) -> None:
    info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
    info.create_system = 3  # Unix, or Archive Utility ignores the mode and nothing is executable
    info.external_attr = (stat.S_IFREG | mode) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    z.writestr(info, data)


def info_plist(ver: str) -> str:
    return f"""<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>RLT Recorder</string>
  <key>CFBundleDisplayName</key><string>RLT Recorder</string>
  <key>CFBundleIdentifier</key><string>com.racingleaguetools.recorder</string>
  <key>CFBundleVersion</key><string>{ver}</string>
  <key>CFBundleShortVersionString</key><string>{ver}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>RltUdpClient</string>
  <key>CFBundleIconFile</key><string>rlt_udp.icns</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>RLT Recorder receives F1 telemetry from your console or PC and serves recorded files on your local network.</string>
</dict>
</plist>
"""


def build_macos(work: Path, ver: str) -> None:
    target = DIST / "rlt-recorder-macos"
    target.mkdir(parents=True, exist_ok=True)

    # Everything here is generated; clearing it keeps a renamed package from
    # leaving its old name behind for someone to download by mistake.
    for old in [*target.glob("*.zip"), *target.glob("*.tar.gz")]:
        old.unlink()
    icon = (ROOT / "deploy" / "macos" / "rlt_udp.icns").read_bytes()
    root = "RLT Recorder.app/Contents/"

    for rid, label in (("osx-arm64", "apple-silicon"), ("osx-x64", "intel")):
        app = work / rid
        publish(DESKTOP, rid, app)

        with zipfile.ZipFile(target / f"rlt-recorder-gui-macos-{label}.zip", "w") as z:
            zip_add(z, root + "Info.plist", info_plist(ver).encode(), 0o644)
            zip_add(z, root + "Resources/rlt_udp.icns", icon, 0o644)
            for f in sorted(app.iterdir()):
                if not f.is_file() or f.suffix == ".pdb":
                    continue
                data = f.read_bytes()
                if data[:4] == MACHO_THIN and not is_signed_macho(data):
                    sys.exit(f"unsigned Mach-O in the {rid} build: {f.name}")
                executable = data[:4] in (MACHO_THIN, MACHO_FAT) or f.name == "createdump"
                zip_add(z, root + "MacOS/" + f.name, data, 0o755 if executable else 0o644)

        cli = work / f"cli-{rid}"
        publish(CLI, rid, cli)
        with tarfile.open(target / f"rlt-recorder-cli-macos-{label}.tar.gz", "w:gz") as t:
            member = t.gettarinfo(cli / "rlt-udp-record", "rlt-udp-record")
            member.mode, member.uid, member.gid, member.uname, member.gname = 0o755, 0, 0, "", ""
            with open(cli / "rlt-udp-record", "rb") as binary:
                t.addfile(member, binary)

    readme = (ROOT / "deploy" / "macos" / "README.txt").read_text(encoding="utf-8")
    # LF: it is read on a Mac.
    (target / "README.txt").write_text(readme.replace("@VERSION@", ver), encoding="utf-8", newline="\n")


def main() -> int:
    ver = version()
    print(f"RLT Recorder {ver} -> {DIST}")

    with tempfile.TemporaryDirectory(prefix="rlt-dist-") as tmp:
        work = Path(tmp)
        build_windows(work)
        build_linux(work)
        build_macos(work, ver)

    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
