#!/usr/bin/env python
"""Judge one render against another the way docs/COMPARING_RENDERS.md says to.

Windowed RMS envelopes at several sizes, plus level. Not samples: two renders can agree on every
note and still correlate near zero sample by sample, because a dense passage is dominated by beating
between simultaneous notes and beating is chaotically sensitive to differences far below audibility.
The sample figure is printed anyway, marked, so nobody has to go and compute it to find out it says
nothing.

Reads 16-bit WAV or raw interleaved float32 (by extension), stereo or mono, and mixes to mono --
a pan disagreement is a separate question and mixing keeps it from looking like a timbre one.

Usage:
    python tools/compare_envelope.py <reference> <candidate> [--rate 32000] [--raw-channels 2]

Exit status is 0 when every window meets its threshold and the level agrees, 1 otherwise.
"""
import argparse, array, math, os, struct, sys, wave

# From docs/COMPARING_RENDERS.md.
# A threshold of None reports the window without gating on it. 4 ms is advisory: its value tracks
# how dense the passage is rather than how right the engine is, so a fixed floor there measures the
# file. See the corpus table in docs/COMPARING_RENDERS.md.
THRESHOLDS = [(4, None), (20, 0.88), (50, 0.90), (250, 0.91), (1000, 0.93)]
LEVEL_DB = 0.5

# A render this quiet is not a render. Guards the failure where both sides are silent and agree
# perfectly -- which has happened here, on twelve effect comparisons at once.
SILENCE = 1e-4


def read_mono(path, rate_hint, raw_channels):
    """Returns (samples, rate). Mixes to mono."""
    if path.lower().endswith(".wav"):
        with wave.open(path, "rb") as w:
            if w.getsampwidth() != 2:
                sys.exit("%s: only 16-bit WAV is handled; convert it." % path)
            frames, channels, rate = w.getnframes(), w.getnchannels(), w.getframerate()
            raw = w.readframes(frames)
        v = struct.unpack("<%dh" % (len(raw) // 2), raw)
        scale = 1.0 / 32768.0
    else:
        with open(path, "rb") as f:
            raw = f.read()
        if len(raw) % 4:
            sys.exit("%s is not a whole number of float32 samples." % path)
        v = array.array("f")
        v.frombytes(raw)
        channels, rate, scale = raw_channels, rate_hint, 1.0

    if channels < 1:
        sys.exit("%s: channel count must be at least 1." % path)
    n = len(v) // channels
    return [sum(v[i * channels:(i + 1) * channels]) * scale / channels for i in range(n)], rate


def rms(x):
    return math.sqrt(sum(t * t for t in x) / len(x)) if x else 0.0


def envelope(x, window):
    return [rms(x[i:i + window]) for i in range(0, len(x) - window + 1, window)]


def correlation(a, b):
    n = min(len(a), len(b))
    if n < 2:
        return float("nan")
    ma, mb = sum(a[:n]) / n, sum(b[:n]) / n
    va = sum((t - ma) ** 2 for t in a[:n])
    vb = sum((t - mb) ** 2 for t in b[:n])
    if va <= 0 or vb <= 0:
        return float("nan")
    return sum((a[i] - ma) * (b[i] - mb) for i in range(n)) / math.sqrt(va * vb)


def best_lag(a, b, rate):
    """Coarse search for a constant offset, which would flatten every window at once.

    A lag that *grows* through the file is a tempo-map fault in whatever produced the render, not a
    difference between engines -- so the two ends are reported separately.
    """
    span = min(rate * 2, len(b) // 4)
    if span < rate // 4:
        return []
    out = []
    for label, start in (("early", len(b) // 8), ("late", len(b) * 3 // 4)):
        if start + span > len(b):
            continue
        window = b[start:start + span]
        best = (0, float("-inf"))
        for lag in range(-rate // 8, rate // 8 + 1, rate // 200):
            at = start + lag
            if at < 0 or at + span > len(a):
                continue
            r = correlation(a[at:at + span], window)
            if r == r and r > best[1]:
                best = (lag, r)
        out.append((label, best[0], best[1]))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("reference")
    p.add_argument("candidate")
    p.add_argument("--rate", type=int, default=32000, help="sample rate for raw input")
    p.add_argument("--raw-channels", type=int, default=2, help="channel count for raw input")
    args = p.parse_args()

    for path in (args.reference, args.candidate):
        if not os.path.exists(path):
            sys.exit("no such file: %s" % path)

    a, rate_a = read_mono(args.reference, args.rate, args.raw_channels)
    b, rate_b = read_mono(args.candidate, args.rate, args.raw_channels)
    if rate_a != rate_b:
        sys.exit("sample rates differ (%d against %d); resample first." % (rate_a, rate_b))

    n = min(len(a), len(b))
    if n == 0:
        sys.exit("one of the renders is empty.")
    if len(a) != len(b):
        print("lengths differ by %d samples (%.3f s); comparing the overlap.\n"
              % (abs(len(a) - len(b)), abs(len(a) - len(b)) / float(rate_a)))
    a, b = a[:n], b[:n]

    ra, rb = rms(a), rms(b)
    if ra < SILENCE or rb < SILENCE:
        print("reference rms %.6f, candidate rms %.6f" % (ra, rb))
        sys.exit("one of the renders is silent -- there is nothing here to agree about.")

    print("%-10s %10s   %s" % ("window", "envelope r", "threshold"))
    ok = True
    for ms, floor in THRESHOLDS:
        window = int(rate_a * ms / 1000.0)
        if window < 1 or len(a) < window * 4:
            print("%7d ms %10s   (too short)" % (ms, "-"))
            continue
        r = correlation(envelope(a, window), envelope(b, window))
        if floor is None:
            print("%7d ms %+10.4f   %s" % (ms, r, "(advisory)"))
            continue
        good = r == r and r >= floor
        ok = ok and good
        print("%7d ms %+10.4f   >= %.2f  %s" % (ms, r, floor, "ok" if good else "UNDER"))

    db = 20.0 * math.log10(rb / ra)
    level_ok = abs(db) <= LEVEL_DB
    ok = ok and level_ok
    print("\nlevel      %+10.2f dB   within %.1f  %s"
          % (db, LEVEL_DB, "ok" if level_ok else "OUT"))

    # Printed and labelled, so its meaninglessness is on the page rather than left to be rediscovered.
    print("sample r   %+10.4f   (not a criterion -- see docs/COMPARING_RENDERS.md)"
          % correlation(a, b))

    lags = best_lag(a, b, rate_a)
    if lags:
        print("\nconstant-offset check:")
        for label, lag, r in lags:
            print("  %-6s best lag %+6d samples (%+.1f ms), r %+.4f"
                  % (label, lag, lag * 1000.0 / rate_a, r))
        if len(lags) == 2 and abs(lags[1][1] - lags[0][1]) > rate_a // 100:
            print("  the offset moves through the file -- suspect a tempo map, not the engine.")

    print("\n%s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
