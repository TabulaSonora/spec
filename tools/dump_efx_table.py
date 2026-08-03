#!/usr/bin/env python
"""Recover the insertion-effect (EFX) directory from your own SCCore.dll.

The 65 GS insertion effects are not anonymous in the file. The engine carries a directory that
names every one of them, keyed by the GS type number `40 03 00` selects and pointing at the DSP
algorithm and the parameter-apply handler that serve it. Nothing external is needed to name them.

The record is 0x28 bytes, and the field `g_fx_type_to_algo_map` points at is not the first one --
the symbol lands on the type key, 12 bytes into the record. Dumping from there reads each effect's
name against the *previous* effect's type key, which is what made the dispatch mapping look like a
scramble with nothing but numbers in it. From the true record start:

    +0x00  char name[12]      display name, space padded
    +0x0C  u16  type          GS type key, (MSB << 8) | LSB
    +0x0E  u16  dispatch      index into g_fx_algo_dispatch
    +0x10  u64  param_apply   per-effect handler mapping the 20 GS parameters to registers
    +0x18  u64  param_defaults  returns a block whose +0x0C holds the 0x1C-byte default parameters
    +0x20  u64  common        one shared handler, identical in all 66 records

There are 66 records: the 65 types the SC-8820 manual lists (00: Thru through 64: PH/AutoWah) plus
a 0xFFFF record with a blank name and a null apply handler, which is the "no effect assigned"
state. Record 66 is not a record -- reading it returns noise, which is how the count is pinned.

The dispatch indices are a scramble of the type order and must be read from here rather than
inferred: Spectrum is type 01 01 but algorithm 6, Humanizer is type 01 03 but algorithm 46.
Algorithm 66 exists in the dispatch table and no record selects it -- see the orphan in FINDINGS.

The names agree with the manual's Insertion Effect List on all 65 types, with two cosmetic
differences: the DLL says "Equalizer" where the manual says "01: Stereo-EQ", and "Lo-Fi" where the
manual says "33: Lo-Fi 1".

Roland-derived output: generate locally, do not redistribute.

Usage:
    python tools/dump_efx_table.py [path-to-SCCore.dll] [--json out.json]
"""
import argparse, hashlib, json, os, struct, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DLL = r"C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"

IMAGE_BASE = 0x180000000
HEADER_SIZE = 0x1000

# g_fx_type_to_algo_map, at the start of the record rather than at the type key the symbol names.
TYPE_MAP_VA = 0x181895660
TYPE_MAP_STRIDE = 0x28
TYPE_MAP_COUNT = 66

# g_fx_algo_dispatch: the function-pointer table of per-algorithm DSP processors.
DISPATCH_VA = 0x181895190
DISPATCH_COUNT = 67

# The record used for "no effect assigned". It is a real record, not a terminator.
NO_EFFECT_KEY = 0xFFFF


def read_at(dll, va, size):
    off = va - IMAGE_BASE - HEADER_SIZE
    if off < 0 or off + size > len(dll):
        sys.exit("address %#x is outside this file -- see docs/DLL_LAYOUT.md" % va)
    return dll[off:off + size]


def read_directory(dll):
    """Reads the 66 records, in file order."""
    entries = []
    for i in range(TYPE_MAP_COUNT):
        rec = read_at(dll, TYPE_MAP_VA + i * TYPE_MAP_STRIDE, TYPE_MAP_STRIDE)
        key, dispatch = struct.unpack("<HH", rec[12:16])
        apply_fn, defaults_fn, common_fn = struct.unpack("<QQQ", rec[16:40])
        entries.append({
            "name": rec[0:12].decode("latin1").rstrip(),
            "type_msb": key >> 8,
            "type_lsb": key & 0xFF,
            "dispatch": dispatch,
            "param_apply": apply_fn,
            "param_defaults": defaults_fn,
            "common": common_fn,
            "assigned": key != NO_EFFECT_KEY,
        })
    return entries


def check(entries, dispatch):
    """Fails loudly rather than emitting a table that only looks right.

    A wrong base still produces 66 plausible rows, so the shape is checked instead of the values:
    names have to be text, dispatch indices have to be in range and distinct, the shared handler
    has to be shared, and algorithm 66 has to be the only unreachable one.
    """
    seen = set()
    common = entries[0]["common"]
    for e in entries:
        if not all(0x20 <= b < 0x7F for b in e["name"].encode("latin1")):
            sys.exit("a record's name is not text -- wrong base address")
        if not 0 <= e["dispatch"] < len(dispatch):
            sys.exit("'%s' dispatches to %d, outside the table" % (e["name"], e["dispatch"]))
        if e["dispatch"] in seen:
            sys.exit("dispatch %d is claimed twice -- wrong stride" % e["dispatch"])
        seen.add(e["dispatch"])
        if e["common"] != common:
            sys.exit("the shared handler is not shared -- wrong record layout")

    unreachable = sorted(set(range(len(dispatch))) - seen)
    if unreachable != [66]:
        sys.exit("expected algorithm 66 alone to be unreachable, got %r" % unreachable)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("dll", nargs="?", default=DEFAULT_DLL, help="path to SCCore.dll")
    ap.add_argument("--json", help="write the table here instead of printing it")
    args = ap.parse_args()

    if not os.path.exists(args.dll):
        sys.exit("SCCore.dll not found at %r -- pass its path as the first argument." % args.dll)
    dll = open(args.dll, "rb").read()

    manifest = json.load(open(os.path.join(ROOT, "tables", "manifest.json")))
    want = manifest["dll"]["sha256"].lower()
    got = hashlib.sha256(dll).hexdigest()
    if got != want:
        sys.exit("DLL sha256 mismatch.\n  expected %s\n  got      %s\n"
                 "This is not the pinned build; offsets may not apply (see docs/DLL_LAYOUT.md)."
                 % (want, got))
    print("DLL verified (sha256 %s..., %d bytes)\n" % (got[:12], len(dll)))

    entries = read_directory(dll)
    dispatch = list(struct.unpack("<%dQ" % DISPATCH_COUNT,
                                  read_at(dll, DISPATCH_VA, DISPATCH_COUNT * 8)))
    check(entries, dispatch)
    for e in entries:
        e["algorithm"] = dispatch[e["dispatch"]]

    if args.json:
        with open(args.json, "w") as f:
            f.write(json.dumps(entries, indent=2) + "\n")
        print("wrote %s" % args.json)
        return

    print("type   dispatch  algorithm  apply     name")
    for e in sorted(entries, key=lambda e: (not e["assigned"], e["type_msb"], e["type_lsb"])):
        key = "%02X %02X" % (e["type_msb"], e["type_lsb"]) if e["assigned"] else "--   "
        # The unassigned record's apply handler is null, which is the point of it.
        apply_fn = "%6x" % (e["param_apply"] - IMAGE_BASE) if e["param_apply"] else "  none"
        print("%s     %3d    %7x    %s    %s"
              % (key, e["dispatch"], e["algorithm"] - IMAGE_BASE, apply_fn,
                 e["name"] or "(no effect)"))
    print("\n%d assigned types, %d algorithms, 1 unreachable (66)"
          % (sum(e["assigned"] for e in entries), DISPATCH_COUNT))


if __name__ == "__main__":
    main()
