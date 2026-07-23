#!/usr/bin/env python
"""
scvx_chorus.py -- the GS chorus, transcribed EXACTLY from SCCore.dll's fx_chorus_stage_l
(@1800851c0). A modulated (LFO-swept) delay: input conditioner (DC-block + one-pole LPF) -> a delay
ring write with feedback -> TWO anti-phase LFO-modulated taps (linear-interpolated) that become the
stereo wet (tap1 -> L, tap2 -> R). One 24-bit sawtooth LFO phase, read at 0 deg (tap1) and 180 deg
(tap2) so the two channels sweep in opposition -> the stereo chorus spread.

Topology is from the decompiled code; the coefficients (LFO rate/depth, base delay, feedback, LPF,
gains) are READ FROM THE LIVE DLL (`scdec chodump`) -- measured engine state, not fitted to audio.
For the GM-default chorus the chorus->reverb send and the fixed R-companion stage are both gated off
(revSend=0, toR=0), so the L stage alone is the whole wet output.
"""
import numpy as np, re, os

RING = 65536
_T = os.path.join(os.path.dirname(__file__), "tables")

# CC93 chorus-send -> send-bus gain at CC93=127. Chorus send measured LINEAR in CC93 (like reverb's
# CC91). Fed from each note's PRE-PAN mono (render_note's `mono`), so the send is pan-independent like
# the engine's bus.
# Calibrated by ISOLATING the engine's own chorus wet return: real_wet = engine(CC93=127) minus
# engine(CC93=0) (the dry path is deterministic, so it cancels), then RMS-matching our CH.chorus()
# output to it -- i.e. matching the engine's measured internal wet, not fitting to any external target.
# The earlier value (0.786) was set against the FULL mix of a sustained note, where wet << dry made
# the level ~2.3x insensitive; the isolated subtraction exposes the true return level. Derived across
# the low-feedback types (Chorus1-4, ShortDelay -- the clean linear regime), which agree to +-2.5%
# (implied 0.333-0.350); the high-feedback types (FeedbackChorus/Flanger/ShortDelayFB) carry a small
# extra nonlinear feedback-path difference and are not used to set the scalar. See docs/FINDINGS.md.
CHO_SEND_127 = 0.3428

def send_gain(cc93):
    return CHO_SEND_127 * (int(cc93) / 127.0)

def load_dump(path):
    """Parse a `scdec chodump` file (snapshot A) -> the chorus L-stage coefficient set."""
    d = {}
    for ln in open(path):
        if ln.startswith('# snapshot B'): break     # only need the first snapshot
        if ln.startswith('L lfo'):
            d['inc']  = int(re.search(r'lfoInc=(-?\d+)', ln).group(1))
            d['lpfA'] = float(re.search(r'lpfA=(\S+)', ln).group(1))
            d['lpfB'] = float(re.search(r'lpfB=(\S+)', ln).group(1))
        elif ln.startswith('L tap1'):
            d['d1'] = int(re.search(r'tap1 depth=(-?\d+)', ln).group(1))
            d['b1'] = int(re.search(r'base=(-?\d+)', ln).group(1))
            d['d2'] = int(re.search(r'tap2 depth=(-?\d+)', ln).group(1))
            d['b2'] = int(re.search(r'tap2 depth=-?\d+ base=(-?\d+)', ln).group(1))
            d['fb'] = float(re.search(r'fbCoef=(\S+)', ln).group(1))
        elif ln.startswith('L gains'):
            d['g_write'] = float(re.search(r'writeIn=(\S+)', ln).group(1))
            d['g_tap']   = float(re.search(r'tapOut=(\S+)', ln).group(1))
    return d

_default_dump = None
def default_dump():
    global _default_dump
    if _default_dump is None:
        _default_dump = load_dump(os.path.join(_T, "chorus_gm_default.txt"))
    return _default_dump

# GS chorus macro (SysEx 40 01 38) -> preset, per RolandSysEx table 14. Each dumped live
# (`scdec chodump <out> <type>`) into tables/chorus_type_<n>_<name>.txt. Same L-stage topology for all
# 8; only lfoInc (rate), tap depth/base (delay), and fbCoef (feedback) change -- Flanger is deep
# feedback (0.875) + small depth, ShortDelay/FB set lfoInc=0 (static, unmodulated delay line). So
# chorus() runs every type unchanged.
CHORUS_TYPE_NAMES = ["Chorus1","Chorus2","Chorus3","Chorus4","FeedbackChorus","Flanger","ShortDelay","ShortDelayFB"]
_type_dumps = {}
def type_dump(t):
    """Chorus coefficients for GS chorus macro `t` (0..7). None -> GS default (Chorus3, macro 2)."""
    if t is None:
        return default_dump()
    global _type_dumps
    if t not in _type_dumps:
        path = os.path.join(_T, f"chorus_type_{t}_{CHORUS_TYPE_NAMES[t]}.txt")
        _type_dumps[t] = load_dump(path) if os.path.exists(path) else default_dump()
    return _type_dumps[t]

def chorus(send_mono, dump):
    """Run the chorus L-stage on the mono chorus-send bus -> (N,2) stereo wet (tap1=L, tap2=R)."""
    n = len(send_mono)
    D = np.zeros(RING, np.float64)
    inc, lpfA, lpfB = dump['inc'], dump['lpfA'], dump['lpfB']
    d1, b1, d2, b2, fb = dump['d1'], dump['b1'], dump['d2'], dump['b2'], dump['fb']
    g_write, g_tap = dump['g_write'], dump['g_tap']
    outL = np.empty(n); outR = np.empty(n)
    phase = 0
    hp = lpf = fb_prev = 0.0
    wp = 0
    M = 0xffff
    HALF = 1 << 23           # 2^23 (24-bit signed range +-2^23)
    SGN = 1 << 24
    for i in range(n):
        # 24-bit signed sawtooth LFO
        phase = (phase + inc) & (SGN - 1)
        ph = phase - SGN if phase >= HALF else phase
        # input conditioner: leaky DC-block + one-pole LPF
        x = send_mono[i] * 0.99804 + hp
        hp = hp + x * -0.003919
        lpf = lpf * lpfA + lpfB * x
        # write into ring with feedback
        D[wp & M] = (fb_prev * fb + lpf) * g_write
        # tap 1 (phase at 0 deg)
        tri1 = abs(ph)
        off1 = ((d1 * tri1) >> 14) + b1
        idx1 = (off1 >> 12) + wp
        f1 = (0x1000 - (off1 & 0xfff)) / 4096.0
        wet1 = (1.0 - f1) * D[(idx1 + 1) & M] + f1 * D[idx1 & M]
        # tap 2 (phase - 180 deg)
        ph2v = (phase - HALF) & (SGN - 1)
        ph2 = ph2v - SGN if ph2v >= HALF else ph2v
        tri2 = abs(ph2)
        off2 = ((d2 * tri2) >> 14) + b2
        idx2 = (off2 >> 12) + wp
        f2 = (0x1000 - (off2 & 0xfff)) / 4096.0
        wet2 = (1.0 - f2) * D[(idx2 + 1) & M] + f2 * D[idx2 & M]
        wp = (wp - 1) & M
        fb_prev = wet1
        outL[i] = wet1 * g_tap
        outR[i] = wet2 * g_tap
    return np.stack([outL, outR], axis=1)
