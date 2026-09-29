#!/usr/bin/env python3
"""Minimal localhost client for the SmashAI Ryubing bridge.

The emulator bridge is enabled by launching Ryujinx with:
    RYUJINX_SMASH_AI_PORT=24842

Examples:
    python smash_ai_client.py --port 24842 ping
    python smash_ai_client.py --port 24842 input --player 1 --lx 32767
    python smash_ai_client.py --port 24842 clear --player 1
    python smash_ai_client.py --port 24842 find-state
    python smash_ai_client.py --port 24842 read-state --out state.bin
    python smash_ai_client.py --port 24842 turbo on
"""

from __future__ import annotations

import argparse
import socket
import struct
from pathlib import Path

MAGIC = 0x31494153
HOST = "127.0.0.1"

BUTTONS = {
    "a": 1 << 0,
    "b": 1 << 1,
    "x": 1 << 2,
    "y": 1 << 3,
    "lstick": 1 << 4,
    "rstick": 1 << 5,
    "l": 1 << 6,
    "r": 1 << 7,
    "zl": 1 << 8,
    "zr": 1 << 9,
    "plus": 1 << 10,
    "minus": 1 << 11,
    "dleft": 1 << 12,
    "dup": 1 << 13,
    "dright": 1 << 14,
    "ddown": 1 << 15,
}

def clamp_axis(value: int) -> int:
    return max(-32768, min(32767, value))

def button_mask(names: list[str]) -> int:
    mask = 0
    for name in names:
        key = name.lower()
        if key not in BUTTONS:
            raise SystemExit(f"unknown button: {name}")
        mask |= BUTTONS[key]
    return mask

def transact(port: int, payload: bytes, timeout: float = 1.0) -> bytes:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.settimeout(timeout)
        sock.sendto(payload, (HOST, port))
        data, _ = sock.recvfrom(65535)
        if len(data) < 8:
            raise RuntimeError("short response from emulator")
        magic, = struct.unpack_from("<I", data, 0)
        if magic != MAGIC:
            raise RuntimeError("invalid bridge response")
        status = data[5]
        if status:
            raise RuntimeError(f"bridge returned status {status}")
        return data

def base(command: int, player: int = 0) -> bytearray:
    packet = bytearray(8)
    struct.pack_into("<I", packet, 0, MAGIC)
    packet[4] = command
    packet[5] = player & 0xFF
    return packet

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=24842)
    sub = parser.add_subparsers(dest="command", required=True)

    sub.add_parser("ping")

    p_input = sub.add_parser("input")
    p_input.add_argument("--player", type=int, default=1, help="1-based player number")
    p_input.add_argument("--button", action="append", default=[])
    p_input.add_argument("--lx", type=int, default=0)
    p_input.add_argument("--ly", type=int, default=0)
    p_input.add_argument("--rx", type=int, default=0)
    p_input.add_argument("--ry", type=int, default=0)

    p_clear = sub.add_parser("clear")
    p_clear.add_argument("--player", type=int, default=1)

    sub.add_parser("find-state")

    p_read = sub.add_parser("read-state")
    p_read.add_argument("--out", type=Path)

    p_turbo = sub.add_parser("turbo")
    p_turbo.add_argument("enabled", choices=["on", "off"])

    args = parser.parse_args()

    if args.command == "ping":
        transact(args.port, bytes(base(1)))
        print("SmashAI bridge is responding.")
        return

    if args.command == "input":
        player = args.player - 1
        if not 0 <= player < 8:
            raise SystemExit("player must be 1..8")
        packet = bytearray(24)
        struct.pack_into("<I", packet, 0, MAGIC)
        packet[4] = 2
        packet[5] = player
        struct.pack_into(
            "<Qhhhh",
            packet,
            8,
            button_mask(args.button),
            clamp_axis(args.lx),
            clamp_axis(args.ly),
            clamp_axis(args.rx),
            clamp_axis(args.ry),
        )
        transact(args.port, bytes(packet))
        return

    if args.command == "clear":
        player = args.player - 1
        packet = base(3, player)
        transact(args.port, bytes(packet))
        return

    if args.command == "find-state":
        data = transact(args.port, bytes(base(6)), timeout=5.0)
        address, size = struct.unpack_from("<Qi", data, 8)
        print(f"state block: 0x{address:X}, {size} bytes")
        return

    if args.command == "read-state":
        data = transact(args.port, bytes(base(4)))
        size, = struct.unpack_from("<i", data, 8)
        state = data[12:12 + size]
        if args.out:
            args.out.write_bytes(state)
            print(f"wrote {len(state)} bytes to {args.out}")
        else:
            print(state.hex())
        return

    if args.command == "turbo":
        packet = bytearray(9)
        struct.pack_into("<I", packet, 0, MAGIC)
        packet[4] = 7
        packet[8] = 1 if args.enabled == "on" else 0
        transact(args.port, bytes(packet))
        return

if __name__ == "__main__":
    main()
