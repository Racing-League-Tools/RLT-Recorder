"""Builds every release package.

    python tools/build_dist.py

dist/release/ gets exactly what goes on a GitHub release, plus SHA256SUMS:

    rlt-recorder-gui-windows-x64.zip
    rlt-recorder-gui-macos-{apple-silicon,intel}.zip
    rlt-recorder-gui-linux-{x64,arm64}.tar.gz
    rlt-recorder-cli-macos-{apple-silicon,intel}.tar.gz
    rlt-recorder-cli-linux-{x64,arm64}.tar.gz

Every archive carries LICENSE.txt and THIRD-PARTY-NOTICES.txt, the latter
generated from the packages the build actually resolved.

Two unpacked copies are also kept for local use: dist/rlt-recorder-gui-windows
(a config.json and dumps/ there survive a rebuild) and dist/rlt-recorder-cli-linux
(both architectures and the installer, ready to copy to a server).

The version comes from Directory.Build.props, the one place it is set.
"""

import hashlib
import io
import json
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
RELEASE = DIST / "release"
DESKTOP = ROOT / "src" / "RltUdpClient.Desktop"
CLI = ROOT / "src" / "RltUdpClient.Cli"
DEPLOY = ROOT / "deploy"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

# Mach-O magics as they appear on disk: thin 64-bit, and a fat (universal) file.
MACHO_THIN = b"\xcf\xfa\xed\xfe"
MACHO_FAT = b"\xca\xfe\xba\xbe"
LC_CODE_SIGNATURE = 0x1D

MTIME = 1767225600  # 2026-01-01, so archives do not change with the clock


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


def binaries(folder: Path) -> list[Path]:
    return sorted(f for f in folder.iterdir() if f.is_file() and f.suffix != ".pdb")


def text(path: Path) -> bytes:
    """A text file as LF bytes. Reading as text folds CRLF, so a CRLF checkout
    cannot break a shell script's shebang or a systemd unit."""
    return path.read_text(encoding="utf-8").encode()


class Archive:
    """A .zip or .tar.gz with explicit Unix modes on every entry: without them
    nothing unpacked on macOS or Linux is executable."""

    def __init__(self, path: Path):
        self.path = path
        if path.suffix == ".zip":
            self.zip = zipfile.ZipFile(path, "w")
            self.tar = None
        else:
            self.zip = None
            self.tar = tarfile.open(path, "w:gz")

    def add(self, name: str, data: bytes, mode: int = 0o644) -> None:
        if self.zip:
            info = zipfile.ZipInfo(name, date_time=(2026, 1, 1, 0, 0, 0))
            info.create_system = 3  # Unix, or Archive Utility ignores the mode
            info.external_attr = (stat.S_IFREG | mode) << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            self.zip.writestr(info, data)
        else:
            info = tarfile.TarInfo(name)
            info.size, info.mode, info.mtime = len(data), mode, MTIME
            self.tar.addfile(info, io.BytesIO(data))

    def add_legal(self, folder: str, notices: bytes) -> None:
        self.add(f"{folder}LICENSE.txt", text(ROOT / "LICENSE"))
        self.add(f"{folder}THIRD-PARTY-NOTICES.txt", notices)

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        (self.zip or self.tar).close()
        print(f"  wrote {self.path.name}")


# --- third-party notices ------------------------------------------------------

def nuspec_copyright(package: str, ver: str) -> str:
    nuspec = next((NUGET / package.lower() / ver).glob("*.nuspec"))
    found = re.search(r"<copyright>([^<]*)</copyright>", nuspec.read_text(encoding="utf-8"))
    holder = found.group(1).strip() if found else package
    return holder if holder.lower().startswith("copyright") else f"Copyright {holder}"


def newest(folder: Path, major: str) -> Path:
    versions = [p for p in folder.iterdir() if p.name.startswith(major + ".")]
    return max(versions, key=lambda p: [int(x) if x.isdigit() else 0 for x in re.split(r"[.-]", p.name)])


def third_party_notices() -> bytes:
    """Licences of everything bundled into the binaries, from the package
    versions the last restore resolved. A package this does not know about stops
    the build, so the notices cannot quietly fall behind the dependencies."""
    assets = json.loads((DESKTOP / "obj" / "project.assets.json").read_text(encoding="utf-8"))
    packages = {name: ver for name, ver in
                (key.split("/") for key, lib in assets["libraries"].items() if lib["type"] == "package")}
    shipped = {n: v for n, v in packages.items()
               if n != "Avalonia.BuildServices" and not n.endswith(".WebAssembly")}

    mit = (DEPLOY / "licenses" / "MIT.txt").read_text(encoding="utf-8")
    apache = (DEPLOY / "licenses" / "Apache-2.0.txt").read_text(encoding="utf-8")
    sections: list[tuple[str, str]] = []
    covered: set[str] = set()

    def names(predicate) -> list[str]:
        found = sorted(n for n in shipped if predicate(n))
        covered.update(found)
        return found

    def listing(found: list[str]) -> str:
        return ", ".join(f"{n} {shipped[n]}" for n in found)

    avalonia = names(lambda n: n.startswith("Avalonia") and n != "Avalonia.Angle.Windows.Natives")
    sections.append((f"Avalonia ({listing(avalonia)})",
                     f"{nuspec_copyright('Avalonia', shipped['Avalonia'])}\n\n{mit}"))

    for package in ("MicroCom.Runtime", "Tmds.DBus.Protocol"):
        found = names(lambda n, p=package: n == p)
        sections.append((listing(found), f"{nuspec_copyright(package, shipped[package])}\n\n{mit}"))

    for package in ("SkiaSharp", "HarfBuzzSharp"):
        found = names(lambda n, p=package: n == p or n.startswith(p + ".NativeAssets"))
        licence = (NUGET / package.lower() / shipped[package] / "LICENSE.txt").read_text(encoding="utf-8")
        sections.append((listing(found), licence))

    native = NUGET / "skiasharp.nativeassets.win32" / shipped["SkiaSharp.NativeAssets.Win32"]
    sections.append(("Skia, HarfBuzz and the libraries they are built with (inside libSkiaSharp and libHarfBuzzSharp)",
                     (native / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8")))

    angle = names(lambda n: n == "Avalonia.Angle.Windows.Natives")
    sections.append((f"ANGLE ({listing(angle)}), Windows only",
                     (NUGET / angle[0].lower() / shipped[angle[0]] / "LICENSE").read_text(encoding="utf-8")))

    serilog = names(lambda n: n.startswith("Serilog"))
    sections.append((listing(serilog), f"Copyright © Serilog Contributors\n\n{apache}"))

    runtime = newest(NUGET / "microsoft.netcore.app.runtime.win-x64", "10")
    sections.append((f".NET runtime {runtime.name}",
                     (runtime / "LICENSE.TXT").read_text(encoding="utf-8") + "\n\n"
                     + (runtime / "THIRD-PARTY-NOTICES.TXT").read_text(encoding="utf-8")))

    for package, title in (("libice6", "libICE"), ("libsm6", "libSM")):
        sections.append((f"{title} (X.Org, from Debian 12), Linux window only",
                         (DEPLOY / "linux" / "lib" / f"COPYING.{package}").read_text(encoding="utf-8")))

    unknown = sorted(set(shipped) - covered)
    if unknown:
        sys.exit(f"no licence entry for bundled package(s): {', '.join(unknown)} — add them to third_party_notices()")

    rule = "=" * 78
    out = ["RLT Recorder includes the following third-party software.",
           "Each component's copyright notice and licence follow.", ""]
    for title, body in sections:
        out += [rule, title, rule, "", body.strip(), "", ""]
    return "\n".join(out).encode()


# --- platforms ----------------------------------------------------------------

def build_windows(work: Path, notices: bytes) -> None:
    out = work / "win-x64"
    publish(DESKTOP, "win-x64", out, single_file=True)

    local = DIST / "rlt-recorder-gui-windows"
    local.mkdir(parents=True, exist_ok=True)
    for f in binaries(out):
        shutil.copy2(f, local / f.name)

    with Archive(RELEASE / "rlt-recorder-gui-windows-x64.zip") as a:
        for f in binaries(out):
            a.add(f"rlt-recorder/{f.name}", f.read_bytes())
        a.add_legal("rlt-recorder/", notices)


def build_linux_cli(work: Path, notices: bytes) -> None:
    local = DIST / "rlt-recorder-cli-linux"
    local.mkdir(parents=True, exist_ok=True)
    installer = sorted(f for f in DEPLOY.iterdir() if f.is_file())

    for rid in ("linux-x64", "linux-arm64"):
        out = work / rid
        publish(CLI, rid, out)
        binary = f"rlt-udp-record-{rid}"  # install.sh picks the binary by these names
        shutil.copy2(out / "rlt-udp-record", local / binary)

        with Archive(RELEASE / f"rlt-recorder-cli-{rid}.tar.gz") as a:
            folder = "rlt-recorder-cli-linux/"
            a.add(folder + binary, (out / "rlt-udp-record").read_bytes(), 0o755)
            for f in installer:
                a.add(folder + f.name, text(f), 0o755 if f.suffix == ".sh" else 0o644)
            a.add_legal(folder, notices)

    for f in installer:
        shutil.copy2(f, local / f.name)


def build_linux_gui(work: Path, ver: str, notices: bytes) -> None:
    """The window as a tarball to unpack anywhere. It carries libICE and libSM:
    Avalonia's X11 backend loads them unconditionally at start-up, and a
    minimal system without them gets a crash instead of a window."""
    extras = DEPLOY / "linux"
    readme = text(extras / "README.txt").replace(b"@VERSION@", ver.encode())

    for rid in ("linux-x64", "linux-arm64"):
        out = work / f"gui-{rid}"
        publish(DESKTOP, rid, out, single_file=True)

        with Archive(RELEASE / f"rlt-recorder-gui-{rid}.tar.gz") as a:
            folder = "rlt-recorder/"
            for f in binaries(out):
                a.add(folder + f.name, f.read_bytes(), 0o755 if f.name == "RltUdpClient" else 0o644)
            for lib in sorted((extras / "lib" / rid).iterdir()):
                a.add(folder + lib.name, lib.read_bytes())
            a.add(folder + "rlt-recorder.png", (extras / "rlt-recorder.png").read_bytes())
            a.add(folder + "add-to-menu.sh", text(extras / "add-to-menu.sh"), 0o755)
            a.add(folder + "README.txt", readme)
            a.add_legal(folder, notices)


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


def build_macos(work: Path, ver: str, notices: bytes) -> None:
    """The window as a signed .app bundle, zipped, and the CLI as a tarball.

    The bundle is ad-hoc signed as a whole with rcodesign. Per-binary
    signatures alone, as the SDK leaves them, make Gatekeeper call a downloaded
    bundle "damaged", with no way past it but Terminal; a sealed bundle gets the
    ordinary "cannot verify the developer" and Open Anyway instead.

    Published single-file so Contents/MacOS holds nothing but Mach-O: codesign
    signs any other file there through extended attributes, and those do not
    survive being zipped on Windows."""
    signer = rcodesign()
    readme = text(DEPLOY / "macos" / "README.txt").replace(b"@VERSION@", ver.encode())

    for rid, label in (("osx-arm64", "apple-silicon"), ("osx-x64", "intel")):
        out = work / rid
        publish(DESKTOP, rid, out, single_file=True)

        bundle = work / f"bundle-{rid}" / "RLT Recorder.app"
        contents = bundle / "Contents"
        (contents / "MacOS").mkdir(parents=True)
        (contents / "Resources").mkdir()
        for f in binaries(out):
            if f.read_bytes()[:4] not in (MACHO_THIN, MACHO_FAT):
                sys.exit(f"non-Mach-O file would land in Contents/MacOS: {f.name}")
            shutil.copy2(f, contents / "MacOS" / f.name)
        shutil.copy2(DEPLOY / "macos" / "rlt_udp.icns", contents / "Resources")
        (contents / "Info.plist").write_text(info_plist(ver), encoding="utf-8", newline="\n")

        print(f"  sign {bundle.name} {rid}")
        subprocess.run([signer, "sign", str(bundle)], check=True, stdout=subprocess.DEVNULL)
        if not (contents / "_CodeSignature" / "CodeResources").is_file():
            sys.exit(f"{rid} bundle came out without a sealed signature")

        with Archive(RELEASE / f"rlt-recorder-gui-macos-{label}.zip") as a:
            for f in sorted(p for p in bundle.rglob("*") if p.is_file()):
                data = f.read_bytes()
                if data[:4] == MACHO_THIN and not is_signed_macho(data):
                    sys.exit(f"unsigned Mach-O in the {rid} bundle: {f.name}")
                a.add(f"{bundle.name}/{f.relative_to(bundle).as_posix()}", data,
                      0o755 if data[:4] in (MACHO_THIN, MACHO_FAT) else 0o644)
            a.add("README.txt", readme)
            a.add_legal("", notices)

        cli = work / f"cli-{rid}"
        publish(CLI, rid, cli)
        with Archive(RELEASE / f"rlt-recorder-cli-macos-{label}.tar.gz") as a:
            folder = "rlt-recorder-cli-macos/"
            a.add(folder + "rlt-udp-record", (cli / "rlt-udp-record").read_bytes(), 0o755)
            a.add_legal(folder, notices)


def write_checksums() -> None:
    lines = []
    for f in sorted(RELEASE.iterdir()):
        if f.name != "SHA256SUMS":
            lines.append(f"{hashlib.sha256(f.read_bytes()).hexdigest()}  {f.name}")
    (RELEASE / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def main() -> int:
    ver = version()
    print(f"RLT Recorder {ver} -> {RELEASE}")

    # Only generated files live here; clearing it keeps a renamed or dropped
    # package from lingering for someone to download by mistake.
    shutil.rmtree(RELEASE, ignore_errors=True)
    RELEASE.mkdir(parents=True)

    with tempfile.TemporaryDirectory(prefix="rlt-dist-") as tmp:
        work = Path(tmp)
        subprocess.run(["dotnet", "restore", str(DESKTOP), "-nologo", "-v", "q"], check=True)
        notices = third_party_notices()

        build_windows(work, notices)
        build_linux_cli(work, notices)
        build_linux_gui(work, ver, notices)
        build_macos(work, ver, notices)

    write_checksums()
    print("done")
    return 0


if __name__ == "__main__":
    sys.exit(main())
