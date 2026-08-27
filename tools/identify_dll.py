#!/usr/bin/env python
"""Identify an arbitrary SCCore.dll build and locate this repo's tables inside it.

The repo's offsets (tables/manifest.json, docs/DLL_LAYOUT.md) are pinned to one build. Roland
shipped several, and they move data freely -- the tables' *contents* are largely stable but their
*offsets* are not, and there is no version resource to read (see docs/DLL_VERSIONS.md). This script
derives a per-build map instead of trusting a pinned one:

  * fingerprint  -- size / MD5 / SHA-1 / SHA-256 / PE timestamp / machine / image base / sections
  * wave ROM     -- scan the self-identifying 1 MB block headers, recover the two bank blobs
  * tables       -- content-locate every tables/*.bin by byte-exact search

Usage:
    python tools/identify_dll.py <path-to-SCCore.dll> [more.dll ...] [--json OUT]

Exact matches give a usable offset for that build. Misses are reported, not guessed at: a table that
does not match byte-for-byte needs deriving for that build (fuzzy "nearby" hits are actively
misleading -- several of these tables begin with a generic ramp that collides all over .rdata).
"""
import os, sys, json, struct, hashlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")

# Every 1 MB wave-ROM block starts with this magic; +0x20 generation label, +0x30 build date.
ROM_MAGIC = bytes.fromhex("A4EBA52BE929")
ROM_BLOCK = 0x100000

def pe_info(b):
    """Parse just enough PE to identify the build and map RVAs to file offsets."""
    e_lfanew = struct.unpack_from("<I", b, 0x3C)[0]
    if b[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("not a PE file")
    machine, nsec = struct.unpack_from("<HH", b, e_lfanew + 4)
    timestamp = struct.unpack_from("<I", b, e_lfanew + 8)[0]
    opt_size = struct.unpack_from("<H", b, e_lfanew + 20)[0]
    opt = e_lfanew + 24
    magic = struct.unpack_from("<H", b, opt)[0]
    is64 = magic == 0x20B
    # ImageBase: PE32+ has no BaseOfData, so it sits 4 bytes earlier than in PE32.
    image_base = struct.unpack_from("<Q" if is64 else "<I", b, opt + (24 if is64 else 28))[0]
    linker = (b[opt + 2], b[opt + 3])
    secs = []
    for i in range(nsec):
        o = opt + opt_size + 40 * i
        name = b[o:o + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", b, o + 8)
        secs.append({"name": name, "va": va, "vsize": vsize, "raw": raw, "rsize": rsize})
    return {
        "machine": {0x14C: "x86", 0x8664: "x64"}.get(machine, "0x%04x" % machine),
        "pe_timestamp": timestamp,
        "image_base": "0x%x" % image_base,
        "linker": "%d.%d" % linker,
        "sections": secs,
    }

def rom_blocks(b):
    """Every wave-ROM block in file order, with its generation label and original build date."""
    out, o = [], b.find(ROM_MAGIC)
    while o >= 0:
        out.append({
            "file_offset": "0x%x" % o,
            "label": b[o + 0x20:o + 0x30].split(b"\0")[0].decode("ascii", "replace").strip(),
            "rom_date": b[o + 0x30:o + 0x40].split(b"\0")[0].decode("ascii", "replace").strip(),
        })
        o = b.find(ROM_MAGIC, o + 1)
    return out

def rom_banks(blocks):
    """Group blocks into maximal runs of consecutive 1 MB blocks -- the bank blobs.

    The engine addresses two banks (16 MB + 8 MB). Builds differ in which order the two blobs are
    stored, so recover them by contiguity rather than assuming the pinned build's order.
    """
    banks, run = [], []
    for blk in blocks:
        off = int(blk["file_offset"], 16)
        if run and off - int(run[-1]["file_offset"], 16) != ROM_BLOCK:
            banks.append(run); run = []
        run.append(blk)
    if run:
        banks.append(run)
    return [{"file_offset": r[0]["file_offset"], "blocks": len(r),
             "size": len(r) * ROM_BLOCK,
             "generations": sorted({x["label"] for x in r})} for r in banks]

def locate_tables(b):
    """Content-locate every cached table. Byte-exact only -- see the module docstring."""
    manifest = json.load(open(os.path.join(T, "manifest.json")))
    found, missing = {}, []
    for e in manifest["cached_tables"]:
        path = os.path.join(T, e["name"])
        if not os.path.exists(path):
            continue
        blob = open(path, "rb").read()
        off = b.find(blob)
        if off < 0:
            missing.append(e["name"]); continue
        occurrences = 1
        nxt = b.find(blob, off + 1)
        while nxt >= 0 and occurrences < 4:
            occurrences += 1; nxt = b.find(blob, nxt + 1)
        found[e["name"]] = {"file_offset": "0x%x" % off, "size": len(blob),
                            "symbol": e.get("symbol"), "occurrences": occurrences}
    return found, missing

def identify(path):
    b = open(path, "rb").read()
    blocks = rom_blocks(b)
    found, missing = locate_tables(b)
    return {
        "filename": os.path.basename(path),
        "size": len(b),
        "md5": hashlib.md5(b).hexdigest(),
        "sha1": hashlib.sha1(b).hexdigest(),
        "sha256": hashlib.sha256(b).hexdigest(),
        "pe": pe_info(b),
        "wave_rom": {"blocks": len(blocks), "banks": rom_banks(blocks),
                     "generations": sorted({x["label"] for x in blocks})},
        "tables_located": found,
        "tables_unresolved": missing,
    }

def report(info):
    pe = info["pe"]
    print("=" * 78)
    print("%s  %d bytes  %s" % (info["filename"], info["size"], pe["machine"]))
    print("  sha256 %s" % info["sha256"])
    print("  md5    %s" % info["md5"])
    print("  PE timestamp %d   image base %s   MSVC linker %s"
          % (pe["pe_timestamp"], pe["image_base"], pe["linker"]))
    print("  sections: %s" % ", ".join("%s@0x%x" % (s["name"], s["raw"]) for s in pe["sections"]))
    wr = info["wave_rom"]
    print("  wave ROM: %d blocks, generations %s" % (wr["blocks"], "+".join(wr["generations"])))
    for bank in wr["banks"]:
        print("    bank at %-12s %2d MB  %s"
              % (bank["file_offset"], bank["size"] >> 20, "+".join(bank["generations"])))
    n_ok, n_bad = len(info["tables_located"]), len(info["tables_unresolved"])
    print("  tables: %d/%d located byte-exact, %d unresolved"
          % (n_ok, n_ok + n_bad, n_bad))
    for name, e in sorted(info["tables_located"].items()):
        dup = "" if e["occurrences"] == 1 else "  (%d occurrences)" % e["occurrences"]
        print("    %-32s %-12s %-24s%s" % (name, e["file_offset"], e["symbol"], dup))
    if info["tables_unresolved"]:
        print("  unresolved (must be re-derived for this build):")
        for name in info["tables_unresolved"]:
            print("    %s" % name)

def main():
    args = [a for a in sys.argv[1:]]
    out = None
    if "--json" in args:
        i = args.index("--json"); out = args[i + 1]; del args[i:i + 2]
    if not args:
        sys.exit(__doc__.strip().splitlines()[-1])
    infos = []
    for path in args:
        if not os.path.exists(path):
            sys.exit("not found: %r" % path)
        info = identify(path)
        infos.append(info)
        report(info)
    if out:
        json.dump(infos if len(infos) > 1 else infos[0], open(out, "w"), indent=1)
        print("\nwrote %s" % out)

if __name__ == "__main__":
    main()
