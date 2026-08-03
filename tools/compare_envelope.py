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


def prefix_squares(x):
    """Running sum of squares, so any window's RMS is O(1)."""
    out = [0.0] * (len(x) + 1)
    acc = 0.0
    for i, t in enumerate(x):
        acc += t * t
        out[i + 1] = acc
    return out


def env_from_prefix(prefix, start, window, hop, count):
    out = []
    for k in range(count):
        s = start + k * hop
        out.append(math.sqrt(max(prefix[s + window] - prefix[s], 0.0) / window))
    return out


def align_delay(pa, pb, rate, n):
    """Small delay search: the shift of the candidate that best matches 20 ms envelopes.

    Absorbs fixed offsets like an event-queue latency. Deliberately bounded to +-32 ms -- anything
    larger is not a delay difference, it is a different performance."""
    window = int(rate * 0.020)
    hop = window // 4
    span = min(n - window, rate * 30)
    count = max(span // hop - 8, 16)
    ref = env_from_prefix(pa, 0, window, hop, count)
    best = (0, -2.0)
    limit = int(rate * 0.032)
    for step, centre in ((max(limit // 16, 1), 0), (4, None)):
        lo = -limit if centre is None else best[0] - limit // 16
        hi = limit if centre is None else best[0] + limit // 16
        if centre is None:
            lo, hi = best[0] - limit // 16, best[0] + limit // 16
        else:
            lo, hi = -limit, limit
        for shift in range(lo, hi + 1, step):
            if shift < 0 or shift + span + window > len(pb) - 1:
                if shift < 0:
                    continue
            cand = env_from_prefix(pb, shift, window, hop, count)
            r = correlation(ref, cand)
            if r == r and r > best[1]:
                best = (shift, r)
    return best[0]


def envelope_psnr(pa, pb, rate, n, window_ms, shift):
    """Peak signal-to-noise of the candidate's envelope against the reference's, delay-corrected.

    The peak is the reference envelope's own maximum, so the number reads as "how far below the
    music's loudest moment the envelope error sits"."""
    window = int(rate * window_ms / 1000.0)
    hop = window // 2
    count = (min(n, len(pb) - 1 - shift) - window) // hop
    if count < 8:
        return float("nan")
    ref = env_from_prefix(pa, 0, window, hop, count)
    cand = env_from_prefix(pb, shift, window, hop, count)
    peak = max(ref)
    if peak <= 0:
        return float("nan")
    err = math.sqrt(sum((ref[i] - cand[i]) ** 2 for i in range(count)) / count)
    if err <= 0:
        return float("inf")
    return 20.0 * math.log10(peak / err)


def _fft(re, im):
    """Iterative radix-2, in place."""
    n = len(re)
    j = 0
    for i in range(1, n):
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j:
            re[i], re[j] = re[j], re[i]
            im[i], im[j] = im[j], im[i]
    length = 2
    while length <= n:
        ang = -2.0 * math.pi / length
        wr, wi = math.cos(ang), math.sin(ang)
        for start in range(0, n, length):
            cr, ci = 1.0, 0.0
            half = length >> 1
            for k in range(start, start + half):
                ur, ui = re[k], im[k]
                vr = re[k + half] * cr - im[k + half] * ci
                vi = re[k + half] * ci + im[k + half] * cr
                re[k], im[k] = ur + vr, ui + vi
                re[k + half], im[k + half] = ur - vr, ui - vi
                cr, ci = cr * wr - ci * wi, cr * wi + ci * wr
        length <<= 1


_FFT_N = 4096


def welch_psd(x, rate, n):
    """Hann-windowed averaged periodogram over up to 48 windows spread through the signal."""
    hann = [0.5 - 0.5 * math.cos(2.0 * math.pi * i / (_FFT_N - 1)) for i in range(_FFT_N)]
    count = min(48, max((n - _FFT_N) // _FFT_N, 1))
    stride = max((n - _FFT_N) // count, 1)
    psd = [0.0] * (_FFT_N // 2)
    for w in range(count):
        s = w * stride
        re = [x[s + i] * hann[i] for i in range(_FFT_N)]
        im = [0.0] * _FFT_N
        _fft(re, im)
        for k in range(_FFT_N // 2):
            psd[k] += re[k] * re[k] + im[k] * im[k]
    return psd


def octave_bands(psd, rate):
    """Sums the PSD into octave bands; returns (label, power) pairs."""
    out = []
    edges = [44.2]
    while edges[-1] < rate / 2:
        edges.append(edges[-1] * 2)
    for lo, hi in zip(edges, edges[1:]):
        klo = max(int(lo * _FFT_N / rate), 1)
        khi = min(int(hi * _FFT_N / rate), _FFT_N // 2)
        if khi <= klo:
            continue
        centre = math.sqrt(lo * hi)
        label = f"{centre/1000:.1f}k" if centre >= 1000 else f"{centre:.0f}"
        out.append((label, sum(psd[klo:khi])))
    return out


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

    # ------------------------------------------------------------------ spectrum and PSNR
    # The phase-tolerant metrics. A free-running modulated effect makes sample and even level
    # comparison phase-dependent (see FINDINGS on the chorus); the averaged spectrum is
    # shift-invariant outright, and the envelope PSNR absorbs fixed delays through a bounded
    # alignment search. These are the gates that survive an engine that cannot be state-matched.
    pa, pb = prefix_squares(a), prefix_squares(b)
    shift = align_delay(pa, pb, rate_a, n)
    print("\ndelay correction: %+d samples (%+.1f ms)" % (shift, shift * 1000.0 / rate_a))

    # Thresholds calibrated on the corpus rather than guessed: the known-good pair measures
    # 23.3/22.6 dB, a known level-class defect 22.8/17.9, a known pitch defect 21.2/25.8. So the
    # 20 ms figure separates nothing and is advisory, the 250 ms figure separates level-class
    # defects and gates at 20, and pitch-class defects are the spectrum's to catch. One good
    # specimen is thin calibration; expect these to move as the corpus grows.
    print("%-10s %10s   %s" % ("PSNR", "dB", "threshold"))
    for ms, floor in ((20, None), (250, 20.0)):
        value = envelope_psnr(pa, pb, rate_a, n, ms, shift)
        if floor is None:
            print("%7d ms %+10.2f   (advisory)" % (ms, value))
            continue
        good = value == value and value >= floor
        ok = ok and good
        print("%7d ms %+10.2f   >= %.0f  %s" % (ms, value, floor, "ok" if good else "UNDER"))

    ref_bands = octave_bands(welch_psd(a, rate_a, n), rate_a)
    cand_bands = octave_bands(welch_psd(b, rate_a, n), rate_a)
    loudest = max(pwr for _, pwr in ref_bands)
    print("\n%-8s %9s %9s %8s" % ("band Hz", "ref dB", "cand dB", "diff"))
    spectrum_floor_db = 45.0
    band_limit_db = 2.0
    for (label, ra_pwr), (_, rb_pwr) in zip(ref_bands, cand_bands):
        if ra_pwr <= 0 or rb_pwr <= 0:
            continue
        ref_db = 10.0 * math.log10(ra_pwr / loudest)
        if ref_db < -spectrum_floor_db:
            print("%-8s %9.1f %9s %8s" % (label, ref_db, "-", "(floor)"))
            continue
        diff = 10.0 * math.log10(rb_pwr / ra_pwr)
        good = abs(diff) <= band_limit_db
        ok = ok and good
        print("%-8s %9.1f %9.1f %+7.2f  %s"
              % (label, ref_db, ref_db + diff, diff, "ok" if good else "OUT"))

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
