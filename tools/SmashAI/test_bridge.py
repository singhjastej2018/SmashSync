#!/usr/bin/env python3
import argparse
import socket
import struct
import time

MAGIC = b"SAI1"
MSG_ACTION = 0x01
MSG_OBSERVE = 0x02
MSG_PING = 0x03
MSG_CLEAR = 0x04

BUTTONS = {
    "a": 1 << 0,
    "b": 1 << 1,
    "x": 1 << 2,
    "y": 1 << 3,
    "l": 1 << 6,
    "r": 1 << 7,
    "zl": 1 << 8,
    "zr": 1 << 9,
    "plus": 1 << 10,
    "minus": 1 << 11,
}

def clamp_axis(v: float) -> int:
    v = max(-1.0, min(1.0, v))
    return round(v * (32767 if v >= 0 else 32768))

def action_packet(player: int, buttons: int, lx: float, ly: float, rx: float, ry: float) -> bytes:
    return struct.pack(
        "<4sBBHqhhhh",
        MAGIC,
        MSG_ACTION,
        player,
        0,
        buttons,
        clamp_axis(lx),
        clamp_axis(ly),
        clamp_axis(rx),
        clamp_axis(ry),
    )

def clear_packet(player: int) -> bytes:
    return MAGIC + bytes([MSG_CLEAR, player])

def main():
    p = argparse.ArgumentParser(description="Smash AI bridge smoke-test client")
    p.add_argument("--port", type=int, default=24872)
    p.add_argument("--player", type=int, default=1, help="0=P1, 1=P2, 2=P3")
    p.add_argument("--seconds", type=float, default=1.0)
    p.add_argument("--button", choices=sorted(BUTTONS), default=None)
    p.add_argument("--lx", type=float, default=0.0)
    p.add_argument("--ly", type=float, default=0.0)
    p.add_argument("--rx", type=float, default=0.0)
    p.add_argument("--ry", type=float, default=0.0)
    p.add_argument("--observe", action="store_true")
    args = p.parse_args()

    addr = ("127.0.0.1", args.port)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(1.0)

    sock.sendto(MAGIC + bytes([MSG_PING]), addr)
    try:
        data, _ = sock.recvfrom(65535)
        if data[:5] != MAGIC + bytes([0x83]):
            raise RuntimeError(f"unexpected ping response: {data[:16]!r}")
        print("bridge: online")
    except socket.timeout:
        raise SystemExit("bridge did not answer; launch Ryujinx with SMASH_AI_PORT set")

    if args.observe:
        sock.sendto(MAGIC + bytes([MSG_OBSERVE]), addr)
        data, _ = sock.recvfrom(65535)
        if len(data) < 8 or data[:4] != MAGIC or data[4] != 0x82:
            raise SystemExit("bad observation response")
        status = data[5]
        length = struct.unpack_from("<H", data, 6)[0]
        if status == 0:
            print(f"observation: {length} bytes, magic={data[8:16]!r}")
        else:
            print("observation: unavailable (companion SSBU state exporter not detected)")

    buttons = BUTTONS.get(args.button, 0)
    packet = action_packet(args.player, buttons, args.lx, args.ly, args.rx, args.ry)
    sock.sendto(packet, addr)
    print(f"holding action on P{args.player + 1} for {args.seconds:.2f}s")
    time.sleep(max(0.0, args.seconds))
    sock.sendto(clear_packet(args.player), addr)
    print("cleared AI override")

if __name__ == "__main__":
    main()
