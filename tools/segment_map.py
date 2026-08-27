#!/usr/bin/env python
"""Map the pinned build's table area onto another build, segment by segment.

Between the 2016 and 2019 releases Roland re-packed `.rdata`: the table *data* is the same (proved by
rendering both DLLs against each other -- see `ab_test` in tables/versions.json) but it was split and
re-ordered into differently arranged segments. A single base address per table therefore does not
exist for the fragmented ones: `g_env_scale_curve`'s first 22 bytes land at one shift and its body at
another, and the eight overlapping `kf_*` windows traverse the same handful of shared segments in
different orders.

So map *ranges*, not tables. This indexes the target image's non-ROM `.rdata`, slides a window across
the pinned build's table area, and merges runs that share a shift into segments. Any pinned offset can
then be translated, and a table that no per-table search could place resolves as a list of pieces.

What stays unmapped is not table data. The cached blobs over-read past the end of the real tables into
the linker's own furniture -- section-contribution names like `.text$mn` and `.CRT$XCA`, and 8-byte
image VAs -- and a VA encodes an address, so it cannot be the same in a build laid out differently.
Every unmapped 16-byte window is absent from the whole target file, not merely from the table area.

Usage:
    python tools/segment_map.py PINNED.dll TARGET.dll [--json OUT] [--window 16]
"""
import os, sys, json, struct, hashlib
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")
ROM_MAGIC = bytes.fromhex("A4EBA52BE929")
ROM_BLOCK = 0x100000

# Shortest run the recovery pass will record. Below this the map fragments into noise for no gain --
# and what is left unplaced at that point is not table data anyway (see the module docstring).
MIN_RECOVERED = 16

def sections(b):
    e = struct.unpack_from("<I", b, 0x3C)[0]
    nsec = struct.unpack_from("<H", b, e + 6)[0]
    opt = e + 24
    optsz = struct.unpack_from("<H", b, e + 20)[0]
    out = []
    for i in range(nsec):
        o = opt + optsz + 40 * i
        name = b[o:o + 8].rstrip(b"\0").decode("ascii", "replace")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", b, o + 8)
        out.append((name, raw, rsize))
    return out

def rom_spans(b):
    """The wave-ROM byte ranges -- excluded, since 24 MB of identical ROM would swamp the map."""
    offs, o = [], b.find(ROM_MAGIC)
    while o >= 0:
        offs.append(o); o = b.find(ROM_MAGIC, o + 1)
    spans, run = [], []
    for x in offs:
        if run and x - run[-1] != ROM_BLOCK:
            spans.append((run[0], run[-1] + ROM_BLOCK)); run = []
        run.append(x)
    if run:
        spans.append((run[0], run[-1] + ROM_BLOCK))
    return spans

def table_area(b):
    """Initialised, non-ROM byte ranges -- where tables can live."""
    roms = rom_spans(b)
    out = []
    for name, raw, rsize in sections(b):
        if name not in (".rdata", ".data") or rsize == 0:
            continue
        cuts = [(raw, raw + rsize)]
        for rl, rh in roms:
            nxt = []
            for lo, hi in cuts:
                if rh <= lo or rl >= hi:
                    nxt.append((lo, hi)); continue
                if lo < rl: nxt.append((lo, rl))
                if rh < hi: nxt.append((rh, hi))
            cuts = nxt
        out += [c for c in cuts if c[1] - c[0] >= 64]
    return out

def build(pinned, target, W=16, step=4):
    """Index the target's table area once, then slide over the pinned build's and merge equal shifts."""
    index = {}
    for lo, hi in table_area(target):
        for i in range(lo, hi - W):
            w = target[i:i + W]
            if w in index:
                index[w] = -1          # ambiguous window: it can anchor nothing
            else:
                index[w] = i

    raw = []
    for lo, hi in table_area(pinned):
        for s in range(lo, hi - W, step):
            w = pinned[s:s + W]
            if w.count(w[0]) == W:     # flat run, matches everywhere
                continue
            o = index.get(w)
            if o is None or o < 0:
                continue
            raw.append((s, s - o))

    segs, cur = [], None
    for s, d in raw:
        if cur and d == cur[2] and s <= cur[1]:
            cur = (cur[0], max(cur[1], s + W), d)
        else:
            if cur: segs.append(cur)
            cur = (s, s + W, d)
    if cur: segs.append(cur)

    out = []
    for a, b, d in segs:
        while a > 0 and 0 <= a - 1 - d < len(target) and target[a - 1 - d] == pinned[a - 1]:
            a -= 1
        while b < len(pinned) and 0 <= b - d < len(target) and target[b - d] == pinned[b]:
            b += 1
        out.append((a, b, d))
    merged = []
    for s in sorted(out):
        if merged and s[2] == merged[-1][2] and s[0] <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(merged[-1][1], s[1]), s[2])
        else:
            merged.append(list(s))
    merged = [tuple(m) for m in merged]

    # Gap fill. A window that occurs in many places anchors nothing, so runs of common data (linear
    # ramps especially) drop out of the map even though they sit inside a segment. Re-test each gap
    # against its neighbours' shifts and accept only on exact byte equality -- proof, not inference.
    shifts = sorted({d for _, _, d in merged})
    filled = []
    for i in range(len(merged) - 1):
        a1, da = merged[i][1], merged[i][2]
        b0, db = merged[i + 1][0], merged[i + 1][2]
        n = b0 - a1
        if not 0 < n <= 16384:
            continue
        for d in [da, db] + shifts:
            t = a1 - d
            if 0 <= t and t + n <= len(target) and target[t:t + n] == pinned[a1:b0]:
                filled.append((a1, b0, d))
                break
    out2 = []
    for s in sorted(merged + filled):
        if out2 and s[2] == out2[-1][2] and s[0] <= out2[-1][1]:
            out2[-1] = [out2[-1][0], max(out2[-1][1], s[1]), s[2]]
        else:
            out2.append(list(s))
    merged = [tuple(m) for m in out2]

    # Second pass over what is still unplaced. The first pass only anchors on windows that occur
    # *once* in the target, which throws away every run of data that happens to appear twice -- and
    # repetitive tables are full of those. Here an ambiguous window is resolved by extent instead:
    # take every occurrence and keep whichever verifies furthest.
    #
    # That is sound because the map's job is to let a reader pull correct bytes out of their own DLL,
    # and correctness is byte equality, which is checked. A range that matches is as good as "the"
    # range, whether or not it is where the linker originally put it.
    index, ambiguous = {}, set()
    target_area = table_area(target)
    for lo, hi in target_area:
        for i in range(lo, hi - W):
            w = target[i:i + W]
            if w in index:
                ambiguous.add(w)
            else:
                index[w] = i

    def verified_run(pinned_at, target_at, cap):
        n = 0
        while n < cap and target_at + n < len(target) and target[target_at + n] == pinned[pinned_at + n]:
            n += 1
        return n

    # Only inside the pinned build's table area. The span between the first and last segment also
    # contains the 24 MB wave ROM, which matches wholesale and would be recorded as one enormous
    # segment -- true, useless, and it swamps everything the map is for.
    pinned_area = table_area(pinned)

    def in_table_area(lo, hi):
        for alo, ahi in pinned_area:
            if lo < ahi and alo < hi:
                yield max(lo, alo), min(hi, ahi)

    holes = []
    for i in range(1, len(merged)):
        start, stop = merged[i - 1][1], merged[i][0]
        if stop > start:
            holes.extend(in_table_area(start, stop))

    recovered = []
    for start, stop in holes:
        position = start
        while position < stop:
            cap = stop - position
            if cap < W:
                break
            w = pinned[position:position + W]
            first = index.get(w)
            if first is None:
                position += 1
                continue
            if w in ambiguous:
                # Enumerate occurrences within the table area only. `find` over the whole image will
                # happily return a hit in the PE header -- the bytes match, so it verifies, but
                # mapping a synth table into the DOS stub is meaningless and brittle.
                candidates, o = [], target.find(w)
                while o >= 0 and len(candidates) < 2048:
                    if any(lo <= o < hi for lo, hi in target_area):
                        candidates.append(o)
                    o = target.find(w, o + 1)
            else:
                candidates = [first]
            best_run, best_at = 0, None
            for o in candidates:
                r = verified_run(position, o, cap)
                if r > best_run:
                    best_run, best_at = r, o
            if best_run >= MIN_RECOVERED and best_at is not None:
                recovered.append((position, position + best_run, position - best_at))
                position += best_run
            else:
                position += 1

    out3 = []
    for s in sorted(merged + recovered):
        if out3 and s[2] == out3[-1][2] and s[0] <= out3[-1][1]:
            out3[-1] = [out3[-1][0], max(out3[-1][1], s[1]), s[2]]
        else:
            out3.append(list(s))

    # The map has to stay a function: one pinned offset, one answer. Clip any residual overlap and
    # assert the result, because a relation here reads as silently wrong data downstream.
    final, previous_end = [], None
    for start, stop, shift in out3:
        if previous_end is not None and start < previous_end:
            start = previous_end
            if start >= stop:
                continue
        final.append((start, stop, shift))
        previous_end = stop
    for i in range(1, len(final)):
        assert final[i - 1][1] <= final[i][0], "segments overlap after clipping"
    for start, stop, shift in final:
        assert target[start - shift:stop - shift] == pinned[start:stop], "segment does not verify"
    return final

def resolve(segs, off, size):
    """Split a pinned range into the target-build pieces that cover it."""
    pieces, pos = [], off
    for a, b, d in segs:
        if b <= pos or a >= off + size:
            continue
        lo, hi = max(a, pos), min(b, off + size)
        if hi <= lo:
            continue
        if lo > pos:
            pieces.append({"pinned_offset": "0x%x" % pos, "length": lo - pos, "target_offset": None})
        pieces.append({"pinned_offset": "0x%x" % lo, "length": hi - lo, "target_offset": "0x%x" % (lo - d)})
        pos = hi
    if pos < off + size:
        pieces.append({"pinned_offset": "0x%x" % pos, "length": off + size - pos, "target_offset": None})
    return pieces

def main():
    args = list(sys.argv[1:])
    out = None; W = 16
    if "--json" in args:
        i = args.index("--json"); out = args[i + 1]; del args[i:i + 2]
    if "--window" in args:
        i = args.index("--window"); W = int(args[i + 1]); del args[i:i + 2]
    if len(args) != 2:
        sys.exit("usage: segment_map.py PINNED.dll TARGET.dll [--json OUT] [--window N]")

    pinned = open(args[0], "rb").read()
    target = open(args[1], "rb").read()
    print("indexing target table area (%s)..." % ", ".join("0x%x..0x%x" % r for r in table_area(target)))
    segs = build(pinned, target, W)
    covered = sum(b - a for a, b, _ in segs)
    area = sum(hi - lo for lo, hi in table_area(pinned))
    print("%d segments, %d distinct shifts, %d of %d table-area bytes mapped (%.0f%%)"
          % (len(segs), len({d for _, _, d in segs}), covered, area, 100.0 * covered / area))

    manifest = json.load(open(os.path.join(T, "manifest.json")))
    print("\n%-30s %-8s %-7s %-9s %s" % ("table", "size", "pieces", "mapped", "first piece -> target"))
    per_table = {}
    for e in manifest["cached_tables"]:
        if not e.get("file_offset"):
            continue
        off, sz = int(e["file_offset"], 16), e["size"]
        pieces = resolve(segs, off, sz)
        got = sum(p["length"] for p in pieces if p["target_offset"])
        first = next((p for p in pieces if p["target_offset"]), None)
        per_table[e["name"]] = {"pieces": pieces, "mapped_bytes": got, "size": sz}
        print("%-30s %-8d %-7d %-9s %s"
              % (e["name"][:30], sz, len([p for p in pieces if p["target_offset"]]),
                 "%d%%" % (100 * got // sz if sz else 0),
                 ("%s (+%s)" % (first["target_offset"], first["pinned_offset"])) if first else "-"))
    if out:
        json.dump({"target_sha256": hashlib.sha256(target).hexdigest(),
                   "segments": [{"pinned_start": "0x%x" % a, "pinned_end": "0x%x" % b,
                                 "shift": ("-0x%x" % -d) if d < 0 else ("0x%x" % d),
                                 "target_start": "0x%x" % (a - d)} for a, b, d in segs],
                   "tables": per_table}, open(out, "w"), indent=1)
        print("\nwrote %s" % out)

if __name__ == "__main__":
    main()
