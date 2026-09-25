"""Reader for RacingLeagueTools UDP dump (.dat) files.

Format, as implemented by UdpPacketProcessor:
  file  = raw DEFLATE stream (RFC 1951 - no gzip/zlib wrapper, no magic)
  body  = repeated [int32 little-endian packet length][raw UDP packet]
No timestamps are stored.
"""

import collections
import glob
import os
import struct
import sys
import zlib

HEADER_FMT = "<HBBBBBQfIIBB"
HEADER_SIZE = 29

TYPE_NAMES = {
    0: "Motion", 1: "Session", 2: "LapData", 3: "Event", 4: "Participants",
    5: "CarSetups", 6: "CarTelemetry", 7: "CarStatus", 8: "FinalClassification",
    9: "LobbyInfo", 10: "CarDamage", 11: "SessionHistory", 12: "TyreSets",
    13: "MotionEx", 14: "TimeTrial", 15: "LapPositions",
}


def inflate(raw: bytes):
    """Inflate a raw deflate stream, tolerating a missing final block."""
    obj = zlib.decompressobj(-15)
    try:
        data = obj.decompress(raw)
        data += obj.flush()
        clean = True
    except zlib.error as exc:
        data = obj.unconsumed_tail and b"" or b""
        print(f"  ! inflate error: {exc}")
        clean = False
    return data, clean, obj.eof


def parse(data: bytes):
    records, offset, truncated = [], 0, False
    while offset + 4 <= len(data):
        (length,) = struct.unpack_from("<i", data, offset)
        offset += 4
        if length <= 0 or offset + length > len(data):
            truncated = True
            break
        records.append(data[offset:offset + length])
        offset += length
    if offset != len(data):
        truncated = True
    return records, truncated


def report(path: str) -> None:
    raw = open(path, "rb").read()
    print(f"\n=== {os.path.basename(path)} ===")
    print(f"  on disk: {len(raw):,} B")

    data, clean, eof = inflate(raw)
    print(f"  inflated: {len(data):,} B   ratio: {len(data) / max(len(raw), 1):.1f}x"
          f"   deflate stream terminated: {eof}")

    records, truncated = parse(data)
    print(f"  packets: {len(records)}   trailing/garbled bytes: {truncated}")

    types = collections.Counter()
    uids = collections.Counter()
    bad = 0
    for rec in records:
        if len(rec) < HEADER_SIZE:
            bad += 1
            continue
        fmt, _, _, _, _, pid, uid = struct.unpack_from("<HBBBBBQ", rec, 0)
        if fmt != 2025:
            bad += 1
            continue
        types[pid] += 1
        uids[f"{uid:X}"] += 1

    if bad:
        print(f"  unparseable packets: {bad}")
    print("  packet types:")
    for pid, n in sorted(types.items()):
        print(f"    {pid:2d} {TYPE_NAMES.get(pid, '?'):<20} {n}")
    print("  session UIDs:")
    for uid, n in uids.items():
        print(f"    {uid}: {n}")


def main(argv) -> int:
    targets = argv[1:]
    if not targets:
        print("usage: read_dat.py <file-or-dir> ...")
        return 2
    files = []
    for t in targets:
        files.extend(sorted(glob.glob(os.path.join(t, "*.dat"))) if os.path.isdir(t) else [t])
    if not files:
        print("no .dat files found")
        return 1
    for f in files:
        report(f)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
