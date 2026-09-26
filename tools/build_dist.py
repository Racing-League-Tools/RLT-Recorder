"""Builds every release package into dist/.

    python tools/build_dist.py

Windows window; Linux window and CLI (x64, arm64), the CLI with its
installer; macOS app bundles plus CLI (Apple Silicon, Intel). Existing config.json and dumps/ in
dist are left alone, so a test install there survives a rebuild.

The version comes from Directory.Build.props, the one place it is set.
"""

import io
import os
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


def tar_add(t: tarfile.TarFile, name: str, data: bytes, mode: int) -> None:
    info = tarfile.TarInfo(name)
    info.size, info.mode, info.mtime = len(data), mode, 1767225600  # 2026-01-01
    t.addfile(info, io.BytesIO(data))


def build_linux_gui(work: Path, ver: str) -> None:
    """The window as a tarball to unpack anywhere. It carries libICE and libSM:
    Avalonia's X11 backend loads them unconditionally at start-up, and a
    minimal system without them gets a crash instead of a window."""
    target = DIST / "rlt-recorder-gui-linux"
    target.mkdir(parents=True, exist_ok=True)
    extras = ROOT / "deploy" / "linux"
    readme = (extras / "README.txt").read_text(encoding="utf-8").replace("@VERSION@", ver)

    for rid in ("linux-x64", "linux-arm64"):
        out = work / f"gui-{rid}"
        publish(DESKTOP, rid, out, single_file=True)

        with tarfile.open(target / f"rlt-recorder-gui-{rid}.tar.gz", "w:gz") as t:
            for f in sorted(out.iterdir()):
                if f.is_file() and f.suffix != ".pdb":
                    tar_add(t, f"rlt-recorder/{f.name}", f.read_bytes(),
                            0o755 if f.name == "RltUdpClient" else 0o644)
            for lib in sorted((extras / "lib" / rid).iterdir()):
                tar_add(t, f"rlt-recorder/{lib.name}", lib.read_bytes(), 0o644)
            for licence in sorted((extras / "lib").glob("COPYING.*")):
                tar_add(t, f"rlt-recorder/{licence.name}", licence.read_bytes(), 0o644)
            tar_add(t, "rlt-recorder/rlt-recorder.png", (extras / "rlt-recorder.png").read_bytes(), 0o644)
            # Read as text, which folds CRLF to LF, so a CRLF checkout cannot
            # break the shebang line.
            script = (extras / "add-to-menu.sh").read_text(encoding="utf-8")
            tar_add(t, "rlt-recorder/add-to-menu.sh", script.encode(), 0o755)
            tar_add(t, "rlt-recorder/README.txt", readme.encode(), 0o644)

    (target / "README.txt").write_text(readme, encoding="utf-8", newline="\n")


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


def rcodesign() -> str:
    """rcodesign (github.com/indygreg/apple-platform-rs) signs macOS bundles
    from any OS. Looked up in $RCODESIGN, tools/bin, then PATH."""
    candidates = [os.environ.get("RCODESIGN"), str(ROOT / "tools" / "bin" / "rcodesign.exe"),
                  str(ROOT / "tools" / "bin" / "rcodesign"), shutil.which("rcodesign")]
    for candidate in candidates:
        if candidate and Path(candidate).is_file():
            return candidate
    sys.exit("rcodesign not found: put it in tools/bin or set RCODESIGN "
             "(https://github.com/indygreg/apple-platform-rs/releases, apple-codesign)")


def build_macos(work: Path, ver: str) -> None:
    """The window as a signed .app bundle, zipped.

    Ad-hoc signed as a whole with rcodesign. Per-binary signatures alone, as the
    SDK leaves them, make Gatekeeper call a downloaded bundle "damaged", with no
    way past it but Terminal; a sealed bundle gets the ordinary "cannot verify
    the developer" and Open Anyway instead.

    Published single-file so Contents/MacOS holds nothing but Mach-O: codesign
    signs any other file there through extended attributes, and those do not
    survive being zipped on Windows."""
    target = DIST / "rlt-recorder-macos"
    target.mkdir(parents=True, exist_ok=True)
    signer = rcodesign()

    # Everything here is generated; clearing it keeps a renamed package from
    # leaving its old name behind for someone to download by mistake.
    for old in [*target.glob("*.zip"), *target.glob("*.tar.gz")]:
        old.unlink()
    for rid, label in (("osx-arm64", "apple-silicon"), ("osx-x64", "intel")):
        out = work / rid
        publish(DESKTOP, rid, out, single_file=True)

        bundle = work / f"bundle-{rid}" / "RLT Recorder.app"
        contents = bundle / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        (contents / "Resources").mkdir()
        for f in out.iterdir():
            if f.is_file() and f.suffix != ".pdb":
                if f.read_bytes()[:4] not in (MACHO_THIN, MACHO_FAT):
                    sys.exit(f"non-Mach-O file would land in Contents/MacOS: {f.name}")
                shutil.copy2(f, contents / "MacOS" / f.name)
        shutil.copy2(ROOT / "deploy" / "macos" / "rlt_udp.icns", contents / "Resources")
        (contents / "Info.plist").write_text(info_plist(ver), encoding="utf-8", newline="\n")

        print(f"  sign {bundle.name} {rid}")
        subprocess.run([signer, "sign", str(bundle)], check=True, stdout=subprocess.DEVNULL)
        if not (contents / "_CodeSignature" / "CodeResources").is_file():
            sys.exit(f"{rid} bundle came out without a sealed signature")

        with zipfile.ZipFile(target / f"rlt-recorder-gui-macos-{label}.zip", "w") as z:
            for f in sorted(p for p in bundle.rglob("*") if p.is_file()):
                data = f.read_bytes()
                if data[:4] == MACHO_THIN and not is_signed_macho(data):
                    sys.exit(f"unsigned Mach-O in the {rid} bundle: {f.name}")
                executable = data[:4] in (MACHO_THIN, MACHO_FAT)
                zip_add(z, f"{bundle.name}/{f.relative_to(bundle).as_posix()}", data,
                        0o755 if executable else 0o644)

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
        build_linux_gui(work, ver)
        build_macos(work, ver)

    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
