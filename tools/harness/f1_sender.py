"""Synthetic F1 2025 UDP telemetry sender.

Emits packets with a valid 29-byte F1 2025 header so RacingLeagueTools.UdpDumper
accepts them, without needing the game. Payloads are semi-repetitive so deflate
ratios resemble real telemetry.
"""

import socket
import struct
import sys
import time

HEADER_FMT = "<HBBBBBQfIIBB"  # 29 bytes, no padding
HEADER_SIZE = struct.calcsize(HEADER_FMT)
assert HEADER_SIZE == 29, HEADER_SIZE

PACKET_FORMAT = 2025

MOTION, SESSION, LAP_DATA, EVENT, PARTICIPANTS = 0, 1, 2, 3, 4
CAR_SETUPS, CAR_TELEMETRY, CAR_STATUS, FINAL_CLASSIFICATION = 5, 6, 7, 8
SESSION_HISTORY = 11

# Realistic on-the-wire sizes per packet type (total, header included).
SIZES = {
    MOTION: 1349,
    SESSION: 753,
    LAP_DATA: 1285,
    EVENT: 45,
    PARTICIPANTS: 1350,
    CAR_TELEMETRY: 1352,
    CAR_STATUS: 1239,
    FINAL_CLASSIFICATION: 1042,
    SESSION_HISTORY: 1155,
}

def _arg(name: str, fallback: str) -> str:
    """Reads --name <value> from argv, falling back to the local default."""
    return sys.argv[sys.argv.index(name) + 1] if name in sys.argv else fallback


HOST = _arg("--host", "127.0.0.1")
PORT = int(_arg("--port", "20777"))

# Seconds to wait between simulated frames. The game sends one frame's worth of
# packets every 1/60 s at the default rate; 0.002 deliberately overdrives it.
FRAME_INTERVAL = 1 / 60 if "--paced" in sys.argv else 0.002


def build(packet_id: int, session_uid: int, frame: int, session_time: float) -> bytes:
    header = struct.pack(
        HEADER_FMT,
        PACKET_FORMAT,   # packetFormat
        25,              # gameYear
        1,               # gameMajorVersion
        0,               # gameMinorVersion
        1,               # packetVersion
        packet_id,       # packetId
        session_uid,     # sessionUID
        session_time,    # sessionTime
        frame,           # frameIdentifier
        frame,           # overallFrameIdentifier
        0,               # playerCarIndex
        255,             # secondaryPlayerCarIndex
    )
    body_len = SIZES.get(packet_id, 512) - HEADER_SIZE
    # Slowly varying, highly repetitive body - like real telemetry, compresses well.
    body = bytes(((frame + i) // 97) & 0xFF for i in range(body_len))
    return header + body


def main() -> int:
    print(f"target: {HOST}:{PORT}")
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sent = {"total": 0}

    def send(packet_id: int, uid: int, frame: int, t: float) -> None:
        sock.sendto(build(packet_id, uid, frame, t), (HOST, PORT))
        sent["total"] += 1
        sent[packet_id] = sent.get(packet_id, 0) + 1

    frame = 0
    t = 0.0

    # Phase 1: main menu - sessionUID 0, must be skipped entirely by the dumper.
    print("phase 1: 20 menu packets (sessionUID=0), expect all skipped")
    for _ in range(20):
        send(LAP_DATA, 0, frame, t)
        frame += 1
        time.sleep(FRAME_INTERVAL)

    # Phase 2: session A - mix of kept and filtered packet types.
    uid_a = 0xA1A2A3A4A5A6A7A8
    print("phase 2: session A, 65 keepable + 200 filterable")
    for i in range(50):
        send(LAP_DATA, uid_a, frame, t)
        send(MOTION, uid_a, frame, t)
        send(CAR_TELEMETRY, uid_a, frame, t)
        if i % 5 == 0:
            send(SESSION, uid_a, frame, t)
        if i % 10 == 0:
            send(PARTICIPANTS, uid_a, frame, t)
        frame += 1
        t += 1 / 60
        time.sleep(FRAME_INTERVAL)

    # Phase 3: session B - different UID, must roll the file over.
    uid_b = 0xB1B2B3B4B5B6B7B8
    print("phase 3: session B, 30 keepable + 50 filterable")
    for i in range(30):
        send(LAP_DATA, uid_b, frame, t)
        send(MOTION, uid_b, frame, t)
        if i == 29:
            send(FINAL_CLASSIFICATION, uid_b, frame, t)
        frame += 1
        t += 1 / 60
        time.sleep(FRAME_INTERVAL)

    sock.close()

    kept_types = {SESSION, LAP_DATA, EVENT, PARTICIPANTS, FINAL_CLASSIFICATION, SESSION_HISTORY}
    print()
    print(f"sent total: {sent['total']}")
    for pid in sorted(k for k in sent if isinstance(k, int)):
        label = "keep" if pid in kept_types else "filter"
        print(f"  type {pid:2d} ({label}): {sent[pid]}")
    print()
    print("expected in dumps: session A = 65 packets, session B = 31 packets")
    print("  (menu packets with UID 0 are dropped before the type filter)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
