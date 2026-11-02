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
CAR_DAMAGE, SESSION_HISTORY = 10, 11

# Realistic on-the-wire sizes per packet type (total, header included).
SIZES = {
    MOTION: 1349,
    SESSION: 753,
    LAP_DATA: 1285,
    EVENT: 45,
    PARTICIPANTS: 1350,
    CAR_TELEMETRY: 1352,
    CAR_STATUS: 1239,
    CAR_DAMAGE: 1041,
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


CARS = 22
STRIDE = {CAR_TELEMETRY: 60, CAR_STATUS: 55, CAR_DAMAGE: 46}


def car_body(packet_id: int, fill) -> bytes:
    """A body laid out per car like the real packet, for the fields the recorder reads."""
    body = bytearray(SIZES[packet_id] - HEADER_SIZE)
    for car in range(CARS):
        fill(body, car * STRIDE[packet_id], car)
    return bytes(body)


def telemetry(frame: int) -> bytes:
    # Everyone at 250 km/h with DRS shut, except car 3: a new top speed on
    # frame 25 and a first DRS-open speed on frame 30.
    def fill(body, at, car):
        speed, drs = 250, 0
        if car == 3 and frame == 25:
            speed = 310
        if car == 3 and frame == 30:
            speed, drs = 300, 1
        struct.pack_into("<H", body, at, speed)
        body[at + 18] = drs
    return car_body(CAR_TELEMETRY, fill)


def status(frame: int) -> bytes:
    # Softs, three laps old; car 5's tyres age a lap on frame 40.
    def fill(body, at, car):
        body[at + 26] = 16
        body[at + 27] = 4 if car == 5 and frame >= 40 else 3
    return car_body(CAR_STATUS, fill)


def damage(frame: int) -> bytes:
    # RLT averages the two most worn tyres and rounds half to even. Car 0
    # reads 12 until frame 45, including 12.5 from frame 35, then 13.
    wear = [10.2, 10.4, 11.6, 11.8]
    if frame >= 20:
        wear = [10.3, 10.5, 11.9, 12.1]
    if frame >= 35:
        wear = [10.4, 10.6, 12.4, 12.6]
    if frame >= 45:
        wear = [10.5, 10.7, 12.6, 12.8]

    def fill(body, at, car):
        struct.pack_into("<4f", body, at, *(wear if car == 0 else [5.0] * 4))
    return car_body(CAR_DAMAGE, fill)


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
    if packet_id in BODIES:
        return header + BODIES[packet_id](frame)
    body_len = SIZES.get(packet_id, 512) - HEADER_SIZE
    # Slowly varying, highly repetitive body - like real telemetry, compresses well.
    body = bytes(((frame + i) // 97) & 0xFF for i in range(body_len))
    return header + body


BODIES = {CAR_TELEMETRY: telemetry, CAR_STATUS: status, CAR_DAMAGE: damage}


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
    print("phase 2: session A, 65 keepable + 12 kept for results + 238 filterable")
    for i in range(50):
        send(LAP_DATA, uid_a, frame, t)
        send(MOTION, uid_a, frame, t)
        send(CAR_TELEMETRY, uid_a, i, t)
        send(CAR_STATUS, uid_a, i, t)
        send(CAR_DAMAGE, uid_a, i, t)
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
    print("expected in dumps: session A = 77 packets, session B = 31 packets")
    print("  (menu packets with UID 0 are dropped before the type filter)")
    print("  session A results packets: telemetry frames 0 1 19 25 30 48,")
    print("  status 0 1 40, damage 0 1 45 (first, after participants, on change)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
