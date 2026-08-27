#!/usr/bin/env python
"""Locate this repo's tables in another SCCore.dll build by cross-referencing code, not content.

Half the pinned build's cached tables cannot be found in another build by byte search (see
docs/DLL_VERSIONS.md §4): the key-follow blobs are over-reads of a shared region, and the small
curves are short monotonic ramps that collide with every other ramp in `.rdata`. What is stable is
the *code* -- the same source compiled twice -- so the instruction that indexes a table sits at the
same point of the same function in both builds.

This diffs two Ghidra xref dumps (tools/ghidra_scripts/DumpXrefs.java) and reads the unknown table
addresses off matched instructions:

  1. seed      -- exported/CRT symbol names, and tables that ARE byte-identical in both builds and
                  are referenced by exactly one function each
  2. propagate -- matched functions' call sites align by order, matching their neighbours
  3. refine    -- match functions whose (anchor tables, matched callers, matched callees)
                  fingerprint is identical and unique on both sides
  4. rank      -- for what is still unmatched, best-match on the data-reference mnemonic sequence
                  plus matched-neighbour context, accepted only with a margin over the runner-up
  5. vote      -- align the reference sequences of each matched pair and read off the wanted table's
                  slot; a table usually has several reference sites and they must agree

Steps 2-4 iterate to a fixpoint, each one making the next one's context sharper.

Usage:
    python tools/match_xrefs.py REF.json REF.dll TARGET.json TARGET.dll [--json OUT] [--min-votes N]

REF must be the pinned build (its table VAs come from tables/manifest.json). TARGET must be a build
registered in tables/versions.json, whose byte-exact table hits are used as the anchors.
"""
import os, sys, json, struct, hashlib, bisect
from collections import defaultdict, Counter
from difflib import SequenceMatcher

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")

GENERIC = ("FUN_", "SUB_", "thunk_FUN", "Unwind@", "FID_conflict")

# ---------------------------------------------------------------- images

def pe_sections(path):
    b = open(path, "rb").read()
    e = struct.unpack_from("<I", b, 0x3C)[0]
    nsec = struct.unpack_from("<H", b, e + 6)[0]
    opt = e + 24
    is64 = struct.unpack_from("<H", b, opt)[0] == 0x20B
    base = struct.unpack_from("<Q" if is64 else "<I", b, opt + (24 if is64 else 28))[0]
    optsz = struct.unpack_from("<H", b, e + 20)[0]
    secs = []
    for i in range(nsec):
        o = opt + optsz + 40 * i
        vsize, va, rsize, raw = struct.unpack_from("<IIII", b, o + 8)
        secs.append((va, vsize, raw, rsize))
    return b, base, secs

class Image:
    """One build: its bytes, its section map, and its Ghidra xref dump."""

    def __init__(self, dump_path, dll_path):
        self.data, self.base, self.secs = pe_sections(dll_path)
        self.dump = json.load(open(dump_path))
        self.funcs = self.dump["functions"]
        self.by_entry = {f["entry"]: f for f in self.funcs}
        self.callers = defaultdict(list)
        for f in self.funcs:
            for c in f["calls"]:
                self.callers[c].append(f["entry"])
        for f in self.funcs:
            f["_m"] = [r["m"] for r in f["refs"]]

    def off_to_va(self, off):
        for va, vsize, raw, rsize in self.secs:
            if raw <= off < raw + rsize:
                return self.base + va + (off - raw)
        return None

    def va_to_off(self, va):
        rva = va - self.base
        for sva, vsize, raw, rsize in self.secs:
            if sva <= rva < sva + max(vsize, rsize):
                o = raw + (rva - sva)
                return o if o < raw + rsize else None
        return None

    def section_of(self, va):
        off = self.va_to_off(va)
        if off is None:
            return "none"
        for sva, vsize, raw, rsize in self.secs:
            if raw <= off < raw + rsize:
                return "%x" % sva
        return "none"

def refs_by_va(image):
    out = defaultdict(list)
    for f in image.funcs:
        for r in f["refs"]:
            out[int(r["to"], 16)].append(f)
    return out

# ---------------------------------------------------------------- scoring

def structural(a, b):
    """Size/shape agreement, tolerant of a compiler-version change."""
    sz = 1.0 - abs(a["size"] - b["size"]) / max(a["size"], b["size"], 1)
    nr = 1.0 - abs(len(a["refs"]) - len(b["refs"])) / max(len(a["refs"]), len(b["refs"]), 1)
    nc = 1.0 - abs(len(a["calls"]) - len(b["calls"])) / max(len(a["calls"]), len(b["calls"]), 1)
    return 0.4 * sz + 0.3 * nr + 0.3 * nc

def context(ref, tgt, a, b, pairs, inv):
    """How much of a's matched neighbourhood coincides with b's."""
    hits = total = 0
    for ac, bc in ((a["calls"], b["calls"]), (ref.callers.get(a["entry"], []), tgt.callers.get(b["entry"], []))):
        mapped = {pairs[x] for x in ac if x in pairs}
        if not mapped:
            continue
        total += len(mapped)
        hits += len(mapped & set(bc))
    return hits / total if total else None

def rank_score(ref, tgt, a, b, pairs, inv):
    seq = SequenceMatcher(a=a["_m"], b=b["_m"], autojunk=False).ratio() if a["refs"] and b["refs"] else 0.0
    base = 0.6 * seq + 0.4 * structural(a, b)
    ctx = context(ref, tgt, a, b, pairs, inv)
    return base if ctx is None else 0.75 * base + 0.25 * ctx

# ---------------------------------------------------------------- stages

def seed_by_name(ref, tgt):
    rn, tn = defaultdict(list), defaultdict(list)
    for f in ref.funcs:
        if not f["name"].startswith(GENERIC):
            rn[f["name"]].append(f)
    for f in tgt.funcs:
        if not f["name"].startswith(GENERIC):
            tn[f["name"]].append(f)
    return {rn[n][0]["entry"]: tn[n][0]["entry"]
            for n in set(rn) & set(tn) if len(rn[n]) == 1 and len(tn[n]) == 1}

def seed_by_anchor(ref, tgt, ref_anchor, tgt_anchor):
    rmap, tmap = refs_by_va(ref), refs_by_va(tgt)
    pairs = {}
    for name in set(ref_anchor) & set(tgt_anchor):
        rf, tf = rmap.get(ref_anchor[name], []), tmap.get(tgt_anchor[name], [])
        if len(rf) == 1 and len(tf) == 1:
            pairs[rf[0]["entry"]] = tf[0]["entry"]
    return pairs

def align(a_items, b_items, key):
    """Positional alignment when the lists are the same length, else a diff alignment."""
    if len(a_items) == len(b_items):
        return list(zip(a_items, b_items))
    sm = SequenceMatcher(a=[key(x) for x in a_items], b=[key(x) for x in b_items], autojunk=False)
    return [(a_items[i + k], b_items[j + k]) for i, j, n in sm.get_matching_blocks() for k in range(n)]

def propagate(ref, tgt, pairs):
    """Matched functions' call sites align by order, so their neighbours match too."""
    taken = set(pairs.values())
    added = 0
    for re_, te_ in list(pairs.items()):
        rf, tf = ref.by_entry.get(re_), tgt.by_entry.get(te_)
        if not rf or not tf:
            continue
        lists = (
            ([c for c in rf["calls"] if c in ref.by_entry], [c for c in tf["calls"] if c in tgt.by_entry]),
            (sorted(x for x in ref.callers.get(re_, []) if x in ref.by_entry),
             sorted(x for x in tgt.callers.get(te_, []) if x in tgt.by_entry)),
        )
        for alist, blist in lists:
            for a, b in align(alist, blist, lambda e: None):
                if a in pairs or b in taken:
                    continue
                fa, fb = ref.by_entry.get(a), tgt.by_entry.get(b)
                if fa and fb and structural(fa, fb) >= 0.7:
                    pairs[a] = b
                    taken.add(b)
                    added += 1
    return added

def anchors_of(f, anchor_by_va):
    return tuple(sorted({anchor_by_va[int(r["to"], 16)]
                         for r in f["refs"] if int(r["to"], 16) in anchor_by_va}))

def refine(ref, tgt, pairs, ra, ta):
    """Match functions whose (anchor set, matched callers, matched callees) fingerprint is unique."""
    taken = set(pairs.values())
    inv = {v: k for k, v in pairs.items()}

    def fp(img, f, label, amap):
        calls = tuple(sorted(label(c) for c in f["calls"]))
        parents = tuple(sorted(label(p) for p in img.callers.get(f["entry"], [])))
        return (anchors_of(f, amap), calls, parents)

    rfp, tfp = defaultdict(list), defaultdict(list)
    for f in ref.funcs:
        if f["entry"] not in pairs:
            rfp[fp(ref, f, lambda e: e, ra)].append(f)
    for f in tgt.funcs:
        if f["entry"] not in taken:
            tfp[fp(tgt, f, lambda e: inv.get(e, "?"), ta)].append(f)

    added = 0
    for k, rs in rfp.items():
        ts = tfp.get(k)
        anch, calls, parents = k
        informative = bool(anch) or any(x != "?" for x in calls) or any(x != "?" for x in parents)
        if not ts or len(rs) != 1 or len(ts) != 1 or not informative:
            continue
        if structural(rs[0], ts[0]) >= 0.5:
            pairs[rs[0]["entry"]] = ts[0]["entry"]
            added += 1
    return added

def rank_match(ref, tgt, pairs, min_score=0.75, min_margin=0.03):
    """Best-match the remainder on reference-mnemonic sequence plus matched-neighbour context."""
    taken = set(pairs.values())
    inv = {v: k for k, v in pairs.items()}
    cands = [g for g in tgt.funcs if g["entry"] not in taken]
    added = 0
    for f in ref.funcs:
        if f["entry"] in pairs or not f["refs"]:
            continue
        span = max(4, len(f["refs"]) // 3)
        pool = [g for g in cands if g["refs"] and abs(len(g["refs"]) - len(f["refs"])) <= span]
        if not pool:
            continue
        scored = sorted(((rank_score(ref, tgt, f, g, pairs, inv), g) for g in pool), key=lambda x: -x[0])
        s1, g1 = scored[0]
        s2 = scored[1][0] if len(scored) > 1 else 0.0
        if s1 >= min_score and s1 - s2 >= min_margin and g1["entry"] not in taken:
            pairs[f["entry"]] = g1["entry"]
            taken.add(g1["entry"])
            added += 1
    return added


def content_estimate(image, blob, chunk=32):
    """Independent, content-only guess at a table's base: vote with unique 32-byte chunks.

    On its own this is unreliable -- that is why these tables are unresolved in the first place --
    but it fails in a completely different way from code cross-referencing. Where the two agree, the
    answer is corroborated by two methods that share no assumptions.
    """
    votes = Counter()
    n = len(blob)
    for s0 in range(0, n - chunk + 1, chunk):
        w = blob[s0:s0 + chunk]
        if len(set(w)) < 5:
            continue
        o = image.data.find(w)
        if o < 0 or image.data.find(w, o + 1) >= 0:
            continue
        votes[o - s0] += 1
    if not votes:
        return None, 0, 0
    base, v = votes.most_common(1)[0]
    return (base if 0 <= base <= len(image.data) - n else None), v, sum(votes.values())

def plausible(ref_blob, cand, dtype):
    """Does the candidate at least have the shape of the table it claims to be?"""
    def vals(buf):
        n = len(buf)
        if dtype.startswith("u16"):
            return list(struct.unpack("<%dH" % (n // 2), buf[:n // 2 * 2]))
        if dtype.startswith(("s16", "i16")):
            return list(struct.unpack("<%dh" % (n // 2), buf[:n // 2 * 2]))
        return list(buf)
    def mono(v):
        return sum(1 for i in range(1, len(v)) if v[i] >= v[i - 1]) / max(len(v) - 1, 1)
    a, b = vals(ref_blob), vals(cand)
    if not a or not b or max(b) == min(b):
        return False
    span = (max(b) - min(b)) / max(max(a) - min(a), 1)
    return abs(mono(a) - mono(b)) < 0.15 and 0.25 <= span <= 4.0


def sibling_check(sib_data, cand, anchor_deltas, base_off):
    """Corroborate a candidate against another image of the SAME release.

    Roland shipped each release as an x86 and an x64 image built minutes apart from one source, and
    their table data is identical -- every one of the byte-exact anchors appears in both. So a
    correctly located table must appear in the sibling too, at a shift consistent with the shifts the
    anchors show for that region. This is the strongest check available here, because it shares no
    assumption with either the code cross-reference or the content search: it is a fact about how the
    release was built.

    Returns (matched_prefix_len, sibling_offset, delta, delta_is_known).
    """
    n = len(cand)
    for take in (n, n // 2, n // 4, 512, 256, 128, 64, 32):
        take = min(take, n)
        if take < 32:
            break
        w = cand[:take]
        o = sib_data.find(w)
        if o >= 0 and sib_data.find(w, o + 1) < 0:
            d = base_off - o
            return take, o, d, d in anchor_deltas
    return 0, None, None, False

def token(image, r, anchor_by_va):
    va = int(r["to"], 16)
    name = anchor_by_va.get(va)
    return (r["m"], "T:" + name) if name else (r["m"], image.section_of(va))

def resolve(ref, tgt, pairs, ra, ta, wanted, sizes):
    """Align each matched pair's reference sequences and vote on each wanted table's base VA.

    A table is a *range*, and the code indexes it at many offsets -- `g_rate_curve` alone is
    referenced from sixteen sites, most of them partway in. Voting only on references to the exact
    base throws nearly all of that away. Instead, any aligned reference landing inside the reference
    build's table votes for `target_va - (ref_va - table_va)`, so every indexing site counts and the
    votes for one table reinforce each other regardless of which element each one names.
    """
    votes, sites = defaultdict(Counter), defaultdict(list)
    ranges = sorted((va, va + sizes.get(name, 1), name) for name, va in wanted.items())
    starts = [r[0] for r in ranges]

    def owner(va):
        i = bisect.bisect_right(starts, va) - 1
        if i >= 0 and ranges[i][0] <= va < ranges[i][1]:
            return ranges[i][2], va - ranges[i][0]
        return None, 0
    for re_, te_ in pairs.items():
        rf, tf = ref.by_entry.get(re_), tgt.by_entry.get(te_)
        if not rf or not tf or not rf["refs"] or not tf["refs"]:
            continue
        rtok = [token(ref, r, ra) for r in rf["refs"]]
        ttok = [token(tgt, r, ta) for r in tf["refs"]]
        if len(rtok) == len(ttok):
            aligned = list(zip(range(len(rtok)), range(len(ttok))))
            ratio = sum(x == y for x, y in zip(rtok, ttok)) / len(rtok)
        else:
            sm = SequenceMatcher(a=rtok, b=ttok, autojunk=False)
            ratio = sm.ratio()
            aligned = [(i + k, j + k) for i, j, n in sm.get_matching_blocks() for k in range(n)]
        if ratio < 0.4:
            continue
        for i, j in aligned:
            rva = int(rf["refs"][i]["to"], 16)
            name, delta = owner(rva)
            if name is None:
                continue
            votes[name][int(tf["refs"][j]["to"], 16) - delta] += 1
            sites[name].append({"func": rf["name"], "ref_site": rf["refs"][i]["at"],
                                "tgt_site": tf["refs"][j]["at"], "elem_offset": "0x%x" % delta,
                                "align": round(ratio, 2)})
    return votes, sites

# ---------------------------------------------------------------- main

def main():
    args = list(sys.argv[1:])
    out, min_votes = None, 1
    if "--json" in args:
        i = args.index("--json"); out = args[i + 1]; del args[i:i + 2]
    sibling = None
    if "--sibling" in args:
        i = args.index("--sibling"); sibling = args[i + 1]; del args[i:i + 2]
    if "--min-votes" in args:
        i = args.index("--min-votes"); min_votes = int(args[i + 1]); del args[i:i + 2]
    if len(args) != 4:
        sys.exit("usage: match_xrefs.py REF.json REF.dll TARGET.json TARGET.dll\n                       [--sibling SAME_RELEASE.dll] [--json OUT] [--min-votes N]")

    ref, tgt = Image(args[0], args[1]), Image(args[2], args[3])
    sib_data = open(sibling, "rb").read() if sibling else None
    manifest = json.load(open(os.path.join(T, "manifest.json")))
    versions = json.load(open(os.path.join(T, "versions.json")))

    ref_all, sizes = {}, {}
    for e in manifest["cached_tables"]:
        sizes[e["name"]] = e.get("size", 1)
        va = int(e["va"], 16) if e.get("va") else (ref.off_to_va(int(e["file_offset"], 16))
                                                   if e.get("file_offset") else None)
        if va:
            ref_all[e["name"]] = va

    tsha = hashlib.sha256(tgt.data).hexdigest()
    build = next((b for b in versions["builds"].values() if b["sha256"] == tsha), None)
    if build is None:
        sys.exit("target build is not in tables/versions.json -- run tools/identify_dll.py first")

    tgt_anchor = {n: tgt.off_to_va(int(o, 16)) for n, o in build["tables"].items()}
    tgt_anchor = {n: v for n, v in tgt_anchor.items() if v}
    ref_anchor = {n: v for n, v in ref_all.items() if n in tgt_anchor}
    wanted = {n: v for n, v in ref_all.items() if n not in tgt_anchor}
    ra = {v: k for k, v in ref_anchor.items()}
    ta = {v: k for k, v in tgt_anchor.items()}

    anchor_deltas = set()
    if sib_data is not None:
        ssha = hashlib.sha256(sib_data).hexdigest()
        sbuild = next((b for b in versions["builds"].values() if b["sha256"] == ssha), None)
        if sbuild:
            for n_, o_ in build["tables"].items():
                if n_ in sbuild["tables"]:
                    anchor_deltas.add(int(o_, 16) - int(sbuild["tables"][n_], 16))
        print("sibling: %s, %d anchor shifts known" % (os.path.basename(sibling), len(anchor_deltas)))

    print("target:  %s (%s)" % (build["filename"], build["description"].split(".")[0]))
    print("anchors: %d tables byte-identical in both builds" % len(ref_anchor))
    print("wanted:  %d tables to locate" % len(wanted))

    pairs = seed_by_name(ref, tgt)
    print("seed:    %d function pairs by symbol name" % len(pairs))
    n = len(pairs)
    for k, v in seed_by_anchor(ref, tgt, ref_anchor, tgt_anchor).items():
        pairs.setdefault(k, v)
    print("         +%d from uniquely-referenced anchor tables" % (len(pairs) - n))

    for it in range(8):
        a = propagate(ref, tgt, pairs)
        b = refine(ref, tgt, pairs, ra, ta)
        c = rank_match(ref, tgt, pairs)
        print("  round %d: +%d propagate, +%d refine, +%d rank -> %d pairs" % (it + 1, a, b, c, len(pairs)))
        if a + b + c == 0:
            break
    print("matched: %d of %d reference-build functions" % (len(pairs), len(ref.funcs)))

    # Cross-validate on the anchors: locate each one by cross-reference as if it were unknown, and
    # check the answer against the offset content search already proved. Same code path, known truth.
    hit = miss = silent = 0
    wrong = []
    for name, rva in ref_anchor.items():
        held_ra = {v: k for k, v in ref_anchor.items() if k != name}
        held_ta = {v: k for k, v in tgt_anchor.items() if k != name}
        v, _ = resolve(ref, tgt, pairs, held_ra, held_ta, {name: rva}, sizes)
        c = v.get(name)
        if not c:
            silent += 1
            continue
        got = c.most_common(1)[0][0]
        if got == tgt_anchor[name]:
            hit += 1
        else:
            miss += 1
            wrong.append((name, "0x%x" % (tgt.va_to_off(got) or 0), "0x%x" % (tgt.va_to_off(tgt_anchor[name]) or 0)))
    print("\nvalidation on the %d anchors (located by xref, checked against content search):" % len(ref_anchor))
    print("  %d correct, %d wrong, %d no matched reference site" % (hit, miss, silent))
    for name, got, want in wrong:
        print("    WRONG %-30s got %s want %s" % (name[:30], got, want))
    if hit + miss:
        print("  precision on tables it does answer for: %.0f%%" % (100.0 * hit / (hit + miss)))

    votes, sites = resolve(ref, tgt, pairs, ra, ta, wanted, sizes)

    meta = {e["name"]: e for e in manifest["cached_tables"]}
    result = {}
    tally = Counter()
    print("\n%-30s %-11s %-8s %-11s %-22s %s"
          % ("table", "xref", "votes", "content", "sibling image", "confidence"))
    for name in sorted(wanted):
        e = meta[name]
        blob_path = os.path.join(T, name)
        blob = open(blob_path, "rb").read() if os.path.exists(blob_path) else b""
        c_off, c_votes, c_total = content_estimate(tgt, blob) if blob else (None, 0, 0)

        c = votes.get(name)
        x_off = None
        if c:
            va, k = c.most_common(1)[0]
            total = sum(c.values())
            x_off = tgt.va_to_off(va)
        else:
            k = total = 0

        shape = "-"
        if x_off is not None and blob:
            cand = tgt.data[x_off:x_off + len(blob)]
            shape = "ok" if len(cand) == len(blob) and plausible(blob, cand, e.get("dtype", "u8")) else "no"

        agree = x_off is not None and c_off is not None and x_off == c_off
        sib_len = sib_off = sib_delta = None
        sib_ok = False
        if sib_data is not None and x_off is not None and blob:
            cand = tgt.data[x_off:x_off + len(blob)]
            sib_len, sib_off, sib_delta, sib_ok = sibling_check(sib_data, cand, anchor_deltas, x_off)

        if x_off is None:
            conf = "unlocated"
        elif sib_ok and sib_len == len(blob):
            conf = "confirmed"
        elif sib_ok and sib_len >= 64:
            conf = "base confirmed"      # right base, blob extent overruns the real table
        elif agree and shape != "no":
            conf = "corroborated"
        elif k == total and k >= 2 and shape == "ok":
            conf = "probable"
        else:
            conf = "weak"
        tally[conf] += 1

        sibdesc = "-" if not sib_len else "%d/%d B d=0x%x%s" % (
            sib_len, len(blob), sib_delta, "" if sib_ok else " ?")
        print("%-30s %-11s %-8s %-11s %-22s %s"
              % (name[:30], ("0x%x" % x_off) if x_off is not None else "-",
                 "%d/%d" % (k, total) if total else "-",
                 ("0x%x" % c_off) if c_off is not None else "-", sibdesc, conf))
        if x_off is not None:
            result[name] = {"file_offset": "0x%x" % x_off, "va": "0x%x" % va,
                            "votes": k, "total_votes": total, "confidence": conf,
                            "content_estimate": ("0x%x" % c_off) if c_off is not None else None,
                            "shape_plausible": shape,
                            "sibling_prefix_bytes": sib_len, "sibling_offset": ("0x%x" % sib_off) if sib_off else None,
                            "sibling_delta_known": sib_ok,
                            "sites": sites[name][:6]}
    print("\n" + ", ".join("%d %s" % (v, k) for k, v in tally.most_common()))
    if out:
        json.dump({"target_sha256": tsha, "function_pairs": len(pairs), "tables": result},
                  open(out, "w"), indent=1)
        print("wrote %s" % out)

if __name__ == "__main__":
    main()
