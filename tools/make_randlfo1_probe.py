#!/usr/bin/env python3
"""A one-note SMF on a tone whose LFO1 is a random shape and whose tone has two partials.

The note sweep cannot see the shared-LFO1 question: none of its 185 melodic cases resolves to a
multi-partial random-LFO1 tone. These do. `Stream` and `Bubble` sit at bank 4 and 5 of program 122
on every map including the SC-55's, so they need nothing but a bank select.

    python3 make_lfo1_probe.py <out.mid> <bank> <program> [note] [velocity]
"""
import struct
import sys


def vlq(n):
    out = bytearray([n & 0x7F])
    n >>= 7
    while n:
        out.insert(0, (n & 0x7F) | 0x80)
        n >>= 7
    return bytes(out)


def track(events):
    body = bytearray()
    for delta, data in events:
        body += vlq(delta) + data
    body += vlq(0) + b"\xff\x2f\x00"
    return b"MTrk" + struct.pack(">I", len(body)) + bytes(body)


def main():
    out, bank, program = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    note = int(sys.argv[4]) if len(sys.argv) > 4 else 60
    velocity = int(sys.argv[5]) if len(sys.argv) > 5 else 100

    ticks = 480          # per quarter
    tempo = 500000       # 120 bpm, so one tick is 1.0417 ms
    events = [
        (0, b"\xff\x51\x03" + struct.pack(">I", tempo)[1:]),
        (0, b"\xf0\x0a\x41\x10\x42\x12\x40\x00\x7f\x00\x41\xf7"),   # GS reset
        (960, bytes([0xB0, 0x00, bank])),                            # bank select MSB
        (0, bytes([0xB0, 0x20, 0x00])),                              # bank select LSB
        (0, bytes([0xC0, program])),
        (96, bytes([0x90, note, velocity])),
        (960, bytes([0x80, note, 0x40])),
    ]
    data = b"MThd" + struct.pack(">IHHH", 6, 0, 1, ticks) + track(events)
    open(out, "wb").write(data)
    print(f"wrote {out}: bank {bank} program {program} note {note} velocity {velocity}")


main()
