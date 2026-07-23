#!/usr/bin/env python
"""Extract the static tables this engine needs directly from your own SCCore.dll.

The repo does NOT ship the DLL-derived data (the copyrighted wave ROM / table bytes). It ships the
MAP: tables/manifest.json records every table's byte-exact offset, size, and identity. This script
reads those offsets from your DLL and writes the tables/*.bin the engine loads.

Usage:
    python tools/extract_tables.py [path-to-SCCore.dll]

It refuses to run unless your DLL matches the pinned SHA-256 in the manifest, so you get exactly the
build everything was reversed against. The reverb/chorus/delay TYPE dumps (tables/*.txt) are produced
separately by the C# harness -- `scdec revdump/chodump` -- not by this script.
"""
import os, sys, json, hashlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")
DEFAULT_DLL = r"C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"

def main():
    dll_path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DLL
    manifest = json.load(open(os.path.join(T, "manifest.json")))
    if not os.path.exists(dll_path):
        sys.exit("SCCore.dll not found at %r -- pass its path as the first argument." % dll_path)
    dll = open(dll_path, "rb").read()

    want = manifest["dll"]["sha256"].lower()
    got = hashlib.sha256(dll).hexdigest()
    if got != want:
        sys.exit("DLL sha256 mismatch.\n  expected %s\n  got      %s\n"
                 "This is not the pinned build; offsets may not apply (see docs/DLL_LAYOUT.md)." % (want, got))
    print("DLL verified (sha256 %s..., %d bytes)" % (got[:12], len(dll)))

    n = 0
    for e in manifest["cached_tables"]:
        if not e.get("file_offset"):
            print("  SKIP %s (no offset)" % e["name"]); continue
        off = int(e["file_offset"], 16)
        size = e["size"]
        data = dll[off:off + size]
        if len(data) != size:
            sys.exit("short read for %s at %s" % (e["name"], e["file_offset"]))
        open(os.path.join(T, e["name"]), "wb").write(data)
        n += 1
    print("wrote %d tables to %s/" % (n, os.path.relpath(T, ROOT)))
    print("note: reverb/chorus/delay TYPE dumps (tables/*.txt) come from `scdec revdump/chodump` -- "
          "build tools/decoder and run those against the same DLL.")

if __name__ == "__main__":
    main()
