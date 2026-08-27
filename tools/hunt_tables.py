#!/usr/bin/env python
"""Locate the pinned build's tables in another release by dual-image content voting.

Half the cached tables cannot be found in another build by a plain byte-exact search: their content
was retuned between releases, so only part of each table still matches (docs/DLL_VERSIONS.md §4).
A partial match is still a location, but a *single* partial match is not trustworthy -- these tables
are short monotonic ramps and `.rdata` is full of ramps.

The test that works exploits the release shipping as an x86/x64 pair:

  1. vote   -- slide several distinct windows of the PINNED blob across the target image; each window
               that matches uniquely votes for `hit - window_offset`. Independent windows agreeing on
               one base is what rules out a ramp collision.
  2. repeat -- do it again, independently, against the release's OTHER image.
  3. check  -- the two bases must differ by roughly the shift the *neighbouring* anchor tables show.

Step 3 is the load-bearing one, and it only works because each search is driven by the pinned build's
own bytes. Asking instead whether a candidate's bytes reappear in the sibling image proves nothing:
the two images of one release are near-copies, so ~100% of arbitrary windows pass that.

Usage:
    python tools/hunt_tables.py PINNED.dll IMAGE_A.dll IMAGE_B.dll [--json OUT]

IMAGE_A and IMAGE_B must be the two images of the SAME release, both registered in
tables/versions.json (their byte-exact hits supply the per-region shifts).
"""
import os, sys, json, hashlib
from collections import Counter

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")

WINDOW_MIN = 32
DELTA_TOL = 0x600          # how far a shift may sit from its neighbours' before it looks wrong

def vote_base(data, blob):
    """Candidate bases from windows at many offsets. Ambiguous windows abstain rather than mislead."""
    n = len(blob)
    width = max(WINDOW_MIN, min(256, n // 8))
    step = max(width, n // 16)
    votes, used = Counter(), 0
    for s in range(0, max(n - width, 1), step):
        w = blob[s:s + width]
        if len(w) < WINDOW_MIN or len(set(w)) < 6:
            continue
        o = data.find(w)
        if o < 0 or data.find(w, o + 1) >= 0:
            continue
        votes[o - s] += 1
        used += 1
    if not votes:
        return None, 0, used
    base, k = votes.most_common(1)[0]
    if base < 0 or base + n > len(data) + n:
        return None, 0, used
    return base, k, used

def agreement(a, oa, b, ob, cap):
    """(leading run, total bytes equal) of the pinned blob against a candidate."""
    run = 0
    while run < cap and oa + run < len(a) and ob + run < len(b) and a[oa + run] == b[ob + run]:
        run += 1
    same = sum(1 for i in range(cap) if oa + i < len(a) and ob + i < len(b) and a[oa + i] == b[ob + i])
    return run, same

def load_build(versions, data):
    sha = hashlib.sha256(data).hexdigest()
    for key, b in versions["builds"].items():
        if b["sha256"] == sha:
            return key, b
    return None, None

def main():
    args = list(sys.argv[1:])
    out = None
    if "--json" in args:
        i = args.index("--json"); out = args[i + 1]; del args[i:i + 2]
    if len(args) != 3:
        sys.exit("usage: hunt_tables.py PINNED.dll IMAGE_A.dll IMAGE_B.dll [--json OUT]")

    pinned = open(args[0], "rb").read()
    A = open(args[1], "rb").read()
    B = open(args[2], "rb").read()
    manifest = json.load(open(os.path.join(T, "manifest.json")))
    versions = json.load(open(os.path.join(T, "versions.json")))

    ka, ba = load_build(versions, A)
    kb, bb = load_build(versions, B)
    if not ba or not bb:
        sys.exit("both images must be registered in tables/versions.json (run tools/identify_dll.py)")
    print("release images: %s  +  %s" % (ka, kb))

    # Per-region shifts, from the tables already located byte-exact in both images.
    shifts = sorted((int(ba["tables"][n], 16), int(ba["tables"][n], 16) - int(bb["tables"][n], 16))
                    for n in set(ba["tables"]) & set(bb["tables"]))
    print("known shifts: %d anchor tables" % len(shifts))

    def shift_ok(off, delta):
        near = sorted(shifts, key=lambda p: abs(p[0] - off))[:5]
        return any(abs(delta - d) <= DELTA_TOL for _, d in near)

    known = set(ba["tables"])
    result, tally = {}, Counter()
    print("\n%-30s %-7s %-11s %-7s %-11s %-7s %-9s %-13s %s"
          % ("table", "size", ka.split('-')[-1], "votes", kb.split('-')[-1], "votes", "shift", "matches", "grade"))
    for e in manifest["cached_tables"]:
        name = e["name"]
        if name in known:
            continue
        path = os.path.join(T, name)
        if not os.path.exists(path):
            continue
        blob = open(path, "rb").read()
        n = len(blob)
        oa, ka_v, _ = vote_base(A, blob)
        ob, kb_v, _ = vote_base(B, blob)

        delta = (oa - ob) if (oa is not None and ob is not None) else None
        ok = delta is not None and shift_ok(oa, delta)
        run = same = 0
        if oa is not None:
            run, same = agreement(pinned, int(e["file_offset"], 16), A, oa, n)

        if ok and ka_v >= 2 and kb_v >= 2:
            grade = "located"
        elif ok and (ka_v >= 2 or kb_v >= 2):
            grade = "probable"
        elif oa is not None:
            grade = "weak"
        else:
            grade = "unlocated"
        tally[grade] += 1

        print("%-30s %-7d %-11s %-7d %-11s %-7d %-9s %-13s %s"
              % (name[:30], n, ("0x%x" % oa) if oa is not None else "-", ka_v,
                 ("0x%x" % ob) if ob is not None else "-", kb_v,
                 ("0x%x" % delta) if delta is not None else "-",
                 "%d/%d" % (same, n), grade))
        if oa is not None:
            result[name] = {ka: "0x%x" % oa, kb: ("0x%x" % ob) if ob is not None else None,
                            "votes": [ka_v, kb_v], "shift": ("0x%x" % delta) if delta is not None else None,
                            "shift_consistent": ok, "leading_run": run, "bytes_equal_to_pinned": same,
                            "size": n, "grade": grade}
    print("\n" + ", ".join("%d %s" % (v, k) for k, v in tally.most_common()))
    if out:
        json.dump(result, open(out, "w"), indent=1)
        print("wrote %s" % out)

if __name__ == "__main__":
    main()
