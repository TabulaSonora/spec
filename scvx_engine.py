#!/usr/bin/env python
"""
scvx_engine -- a first playable reimplementation of the Sound Canvas VA voice,
built entirely from the reverse-engineered pieces (NO SCCore.dll at render time):

  (map,program,note,vel) --scvx_directory--> wave# + ROM coords + root key + loop
      --block-FP DPCM codec--> PCM  --pitch/loop playback--> TVF --> TVA envelope --> mix
  Drums: note --drum kit table--> tone# --> wave, at natural rate x coarse-pitch offset.

What is exact (reversed from the DLL, validated by A/B against the real engine):
  * sample selection / key+velocity zones / tuning root  (scvx_directory), including the
    THREE tone spaces -- melodic, drum, and the indirect 0x6000+ entries (Violin/Viola/
    Cello/Strings), whose second slot is a mono-mode alternate articulation, not a layer
  * the block-floating-point DPCM codec                  (predictor<<(scale+10))
  * loop points: data=[loop,start], sustain loop=[end,start]; loop whenever that region exists
  * the sampler's 4-tap FIR interpolation (ICOEF), and its three variants incl. ping-pong
  * internal sample rate 32000 Hz; 100 Hz control tick
  * TVA envelope: full segment-rate machine (4 segments + release) with the engine's
    g_env_shape interpolation curve -- segment_time_ms from g_rate_curve + env_rate_scale
  * the LEVEL chain: partial level, the velocity-crossfaded per-partial level (voice+0x164),
    zone level, tone level, x2 -- reproduces the engine's own per-voice gain to 5.4e-05
  * TVF: Chamberlin SVF, 4 taps, f = 2^(C/16384 - 15) via voice+0xcc; resonance from block[0x30]
  * pan: per-partial block[0x0f] and the drum kit's +0x280 plane, exact against the ROM table
  * drum kit table (note -> tone#, level, coarse pitch, mute group, pan)
  * LFO: BOTH engines (tone-common + per-partial), rate/depth/delay/fade-in, routed to all
    THREE destinations -- pitch (milli-semitones, 1000 = 1 semitone), TVF (added to the
    15-bit cutoff) and TVA (a fraction of 0x7f00, multiplicative). Validated bit-exact
    against the engine's own LFO objects via `scdec lfotrace`: 1194/1194 control ticks,
    2 LFOs x 3 destinations, zero difference.
Still approximate / not yet implemented:
  * LFO shapes 1/2/3 are the prng_lfsr (Galois LFSR) random ones; they need the engine's RNG
    state, so lfo_wave returns 0 for them rather than substituting invented noise
  * no pitch envelope; no effects (rendered dry -- the A/B harness sets CC91/93 = 0 to match)

Run with the numpy venv:  scratchpad/venv/Scripts/python.exe scvx_engine.py
"""
import numpy as np, wave, os, struct
import scvx_directory as D
import scvx_partials as P

SR    = 32000
CONST = 7.450580596923828e-09          # predictor -> [-1,1] (the codec's output scale)
BASE  = {0: 0x92700, 1: 0x1092730}     # ROM base file-offset per bank
DLL   = open(os.environ.get("SCVX_DLL",
                            r"C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"), "rb").read()

# ---- static synth curve tables (exported from SCCore.dll .rdata) ----
_T   = os.path.join(os.path.dirname(__file__), "tables")
LVL  = np.frombuffer(open(os.path.join(_T, "curve_level_2a00.bin"), "rb").read(), np.uint16).astype(np.int32)  # g_level_curve: stage byte -> 16-bit log attenuation
AH   = np.frombuffer(open(os.path.join(_T, "curve_amp_hi_2ba0.bin"), "rb").read(), np.uint16).astype(np.int64) # g_amp_curve_hi
AL   = np.frombuffer(open(os.path.join(_T, "curve_amp_lo_2da0.bin"), "rb").read(), np.uint16).astype(np.int64) # g_amp_curve_lo

# ---- TVF envelope tables (partial_compute_filter / tvf_env_level_conv) ----
_FED  = open(os.path.join(_T, "curve_filtenvdepth_2f00.bin"), "rb").read()   # base 0x1819a2f00
_PEN  = open(os.path.join(_T, "curve_pitchenv_2578.bin"), "rb").read()       # base 0x1819a2578
KFTVF = np.frombuffer(open(os.path.join(_T, "kf_tvfenv_02a20.bin"), "rb").read(), np.uint16)  # g_kf_tvfenv (s16)
RESOC = np.frombuffer(open(os.path.join(_T, "curve_reso_2b88.bin"), "rb").read(), np.int16)   # g_reso_curve
KFTVFR= np.frombuffer(open(os.path.join(_T, "kf_tvfrate_03920.bin"), "rb").read(), np.uint8)  # g_kf_tvfrate
def _u16at(buf, base_addr, addr):
    o = addr - base_addr
    return buf[o] | (buf[o + 1] << 8)
def _c2890(i):     # DAT_1819a2890, byte table inside the pitchenv export
    return _PEN[0x2890 - 0x2578 + (i & 0xff)]

def tvf_env_offsets(raw, key, vel):
    """partial_compute_filter + tvf_env_level_conv, as decompiled.
       Returns (peak, [5 stage offsets relative to peak]) in voice+0x1f0 cutoff units."""
    kfv = int(KFTVF[((raw[0x32] & 0xf) * 0x80 + (key & 0x7f))])          # s16 env-depth key-follow
    if kfv >= 0x8000: kfv -= 0x10000
    d = int(raw[0x33])
    if d < 0x40:
        val = _u16at(_FED, 0x1819a2f00, 0x1819a3028 + (0x80 - d * 2))
        peak = -((kfv * val) >> 9) if kfv >= 0 else ((-kfv * val) >> 9)
    else:
        val = _u16at(_FED, 0x1819a2f00, 0x1819a2fa8 + d * 2)
        peak = ((kfv * val) >> 9) if kfv >= 0 else -((-kfv * val) >> 9)
    # resonance scaling (DAT_181a1f5c0 / _5c2)
    r = int(raw[0x4a]) - 0x40
    if r == 0:
        c0, c2 = 0x7fff, 0
    else:
        s = 0x7f - min(127, vel)
        c0 = (int(RESOC[abs(r) & 0x3f]) * s - 0x7fff) if r < 0 else (0x7fff - int(RESOC[r & 0x3f]) * s)
        c2 = 0
        if c0 < 0: c0, c2 = -c0, 1
    c4 = raw[0x38] | (raw[0x39] << 8)                                    # DAT_181a1f5c4 = block[0x38] u16
    def conv(lvl):
        off = int(lvl) - 0x40
        a = abs(off)
        if a == 0: return peak
        flag = c2 if off >= 0 else (~c2 & 1)
        delta = ((((c4 * c0) & 0x7fffffff) >> 15) * _c2890(a)) >> 7
        return peak + delta if flag == 0 else peak - delta
    # Time order mirrors the TVA: voice+0x3e<-block[0x3a] is the CURRENT segment target and
    # +0x1e4/+0x1e6/+0x1e8 <- 0x3b/0x3c/0x3d the next ones, while +0x52<-block[0x3e] is the
    # RELEASE target. So: 4 segments (0x3a..0x3d) + release (0x3e). Stored relative to peak.
    segs = [conv(raw[o]) - peak for o in (0x3a, 0x3b, 0x3c, 0x3d)]
    rel  = conv(raw[0x3e]) - peak
    return peak, segs, rel

def compute_tvf_env(raw, vel, hold, tail, key):
    """Cutoff-byte trajectory over time: base + env_offset/256, shaped by the same segment
       machine as the TVA (rates block[0x3f..0x42], release 0x43; g_env_shape interpolation)."""
    nsamp = int((hold + tail) * SR)
    peak, segs, rel = tvf_env_offsets(raw, key, vel)
    b8 = env_rate_scale((int(KFTVFR[raw[0x46] * 0x80 + (key & 0x7f)]) - 0x80) & 0xff, raw[0x48])
    # tvf_compute_env_rates @1800616f0 uses TWO velocity level-scale factors, not one:
    #   DAT_181a1f5ba = env_level_scale(vel, block[0x4b])  -> segments 0,1   (lines 40506/40527)
    #   DAT_181a1f5bc = env_level_scale(vel, block[0x4c])  -> segments 2,3 + release (40550/40564/40583)
    # We were using 0x4b for all four. On patches where 0x4c != 0x4b (Harpsichord: 0x4c=0x3c ->
    # level-scale 370 vs 256) that made segments 2,3 run ~1.45x too FAST, so the filter closed too
    # early in the sustain and the note darkened/"cut off" well before the DLL did -- worst at high
    # keys, where the segments are already short. Splitting bv01/bv23 reproduces the engine's own
    # +0xec env-level slopes exactly (measured via tvftrace: seg2/3 slope error 1.45x -> 1.00x).
    bv01 = env_level_scale(vel, raw[0x4b])                       # segments 0, 1
    bv23 = env_level_scale(vel, raw[0x4c])                       # segments 2, 3, release
    st  = [_seg_ms(raw[0x3f + i], b8, bv01 if i < 2 else bv23) for i in range(4)]
    # The RELEASE uses its OWN rate key-follow (DAT_181a1f5be @tvf_compute_env_rates line 40366):
    # env_rate_scale(g_kf_tvfrate2[block[0x47]*0x80 + key] - 0x80, block[0x49]) -- distinct row byte
    # (0x47) and mod byte (0x49) from the main rate's 0x46/0x48. g_kf_tvfrate2 is a separate symbol,
    # but its layout is contiguous with g_kf_tvfrate and row 0 is byte-identical (the default linear
    # ramp), so KFTVFR reproduces it wherever block[0x47]==0 (validated: Harpsichord release slope
    # 12399/s with the old b8 -> 10425/s, matching the DLL's own +0xec release trace at ~10440/s).
    # Patches with block[0x47] != 0 would need the distinct g_kf_tvfrate2 rows exported; none seen yet.
    b8r = env_rate_scale((int(KFTVFR[raw[0x47] * 0x80 + (key & 0x7f)]) - 0x80) & 0xff, raw[0x49])
    rms = _seg_ms(raw[0x43], b8r, bv23)
    off = np.zeros(nsamp, np.float64)
    hi = min(int(hold * SR), nsamp)
    # The env's CURRENT level starts at ZERO, not at the release level. partial_compute_filter
    # makes all five targets relative to `peak` (+0x3e/+0x52/+0x1e4/+0x1e6/+0x1e8 each -= peak),
    # then zeroes the peak register (+0x3c = 0) and seeds the running level from it (+0xec = +0x3c).
    # `peak` itself is folded into the BASE cutoff instead: voice+0x1f0 = min(0x7fff,
    # block[0x2f]*0x100 + peak).
    t, prev = 0.0, 0.0
    for i in range(4):
        T = max(st[i] / 1000.0, 0.002)
        lo, hi2 = int(t * SR), min(int((t + T) * SR), nsamp)
        if hi2 > lo:
            # TVF segments are ALWAYS linear: FUN_1800616f0 writes the literal 0x4000 shape word
            # for every stage (unlike the TVA, where bit 7 of the rate byte picks the shape).
            off[lo:hi2] = _seg_curve((np.arange(hi2 - lo)) / (T * SR), prev, segs[i], linear=True)
        prev, t = segs[i], t + T
        if t >= hold: break
    if int(t * SR) < hi: off[int(t * SR):hi] = prev
    cur = off[hi - 1] if hi > 0 else prev
    ri = min(nsamp - hi, max(1, int(rms / 1000.0 * SR)))
    if ri > 0:
        off[hi:hi + ri] = _seg_curve(np.arange(ri) / max(rms / 1000.0 * SR, 1e-9), cur, rel, linear=True)
    off[hi + ri:] = rel
    # Stage B (FUN_180084200): cutoff = (part_cutoff_default + part_cutoff - 0x80)*256 + base + env,
    # clamped to 0..0x7fff. Both part terms are 0x40 when neutral, so they cancel here.
    base = min(0x7fff, raw[0x2f] * 0x100 + peak)        # voice+0x1f0
    return np.clip(base + off, 0, 0x7fff)               # 15-bit cutoff sum over time

# block[0x31] -> filter RESPONSE TYPE. partial_compute_filter rejects a type unless
# `!(t > 2 && (uint8)(t-4) > 2)`, i.e. only {0,1,2,4,5,6} are valid (3 and 7 bypass), then stores
# g_filter_type_coef[t] at partial+0x1ec. Voice start copies that to DAT_181a71b60[v], and
# FUN_18008ce70 turns bits 10-11 into one of four taps of the SAME state-variable filter:
#   0x000 -> FUN_18008d0a0  low                 (lowpass)
#   0x400 -> FUN_18008d520  band                (bandpass)
#   0x800 -> FUN_18008d2d0  in - q*band - low   (highpass)
#   0xc00 -> FUN_18008d740  low - high          (notch)
FILTTYPE = np.frombuffer(open(os.path.join(_T, "curve_filttype_987b00.bin"), "rb").read(), np.uint32)
_TAP = {0x000: 'lp', 0x400: 'bp', 0x800: 'hp', 0xc00: 'notch'}

def tvf_tap(ftype):
    """block[0x31] -> 'lp'/'hp'/'bp'/'notch', or None when the partial bypasses the filter."""
    ftype = int(ftype) & 0xff
    if ftype > 2 and ((ftype - 4) & 0xff) > 2:
        return None
    return _TAP[int(FILTTYPE[ftype]) & 0xc00]

def apply_tvf_varying(sig, cutoff15, ftype, reso_byte, block=320):
    """The engine's filter: a Chamberlin state-variable filter, verified from the DISASSEMBLY of
       FUN_18008d9a0 (Ghidra's C was accurate; the SSE decodes to exactly this, with xmm7=low,
       xmm6=band, xmm5=f, xmm4=q -- no prescale, no oversampling, no reordering):

           low  += f * band;                       /* LP tap stores `low` right here */
           high  = in - (q * band + low);          /* HP tap */
           band += f * high;                       /* BP tap; notch = low - high    */

       Both coefficients come from the engine, so NO calibrated constant remains in this path:
           f = tvf_f_coef(partial+0xcc)      -- exponential, = 2^(C/16384 - 15)
           q = (partial+0xdc >> 3) / 16384   -- = 1/Q, neutral resonance gives exactly 1.0

       The earlier failed port fed `f` the *linear* cutoff units, which reach ~1.9 and make the
       state matrix [[1,f],[-f,1-qf-f^2]] diverge (real eigenvalue -2.20). Through the exponential
       the same patches give f = 0.05..1.23, comfortably stable. Coefficients refresh per 320-sample
       control tick, matching the engine's 100 Hz rate; the engine additionally slews them over
       2-40 ms via voice_ctrl_ramp_c/d, which is not modelled."""
    tap = tvf_tap(ftype)
    if tap is None:
        return sig.astype(np.float32)
    xs = sig.astype(np.float64).tolist()
    n = len(xs)
    out = [0.0] * n
    low = band = 0.0
    for i in range(0, n, block):
        j = min(i + block, n)
        C = tvf_cutoff_units(float(np.mean(cutoff15[i:j])), reso_byte)
        f = tvf_f_coef(C)
        q = tvf_q(C, reso_byte, ftype)
        for k in range(i, j):
            low += f * band
            high = xs[k] - (q * band + low)
            band += f * high
            out[k] = low if tap == 'lp' else high if tap == 'hp' else band if tap == 'bp' else low - high
    return np.asarray(out, dtype=np.float32)

KFLVL = np.frombuffer(open(os.path.join(_T, "kf_tvalevel_026a0.bin"), "rb").read(), np.uint8)  # g_kf_tvalevel
VELC  = np.frombuffer(open(os.path.join(_T, "curve_vel_2b00.bin"),  "rb").read(), np.uint8)   # g_vel_curve

def tva_base_level(raw, vel, key, zone_lvl=127, tone_lvl=127):
    """tva_compute_base_level @180060960, implemented as decompiled:
         base = ~g_level_curve[block[0x53]]                       (partial level)
                +/- g_vel_curve[kf term]*0x100                    (level key-follow 0x54/0x55)
                -  g_level_curve[velocity]                        (voice+0x164)
                -  g_level_curve[zone level]                      (voice+0x140)
                -  g_level_curve[tone level]                      (voice+0x167)
       Each subtraction clamps to >= 1. Same-sign key-follow adds, mixed sign subtracts.

       The last two used to be passed as a hardcoded 127 (i.e. dropped), which is what left a
       per-voice 1.15-2.06x residual against the engine's own gain. They are static:
       `zone_lvl` = D.zone_level(msamp, key, keyCenter) -- the multisample's per-key-zone level
       plane (+0x6c); `tone_lvl` = D.tone_level(tone#) -- the tone header's master level (+0x0c)."""
    base = 0xffff - int(LVL[raw[0x53]])
    kf = int(KFLVL[raw[0x54] * 0x80 + (key & 0x7f)])
    if kf >= 128: kf -= 256                                   # signed char
    d = int(raw[0x55]) - 0x40
    if d != 0 and kf != 0:
        sc = int(SCURVE[abs(d) & 0xff])
        term = int(VELC[((abs(kf) * sc * 2) >> 8) & 0xff]) * 0x100
        same = (d < 0 and kf < 0) or (d >= 0 and kf >= 0)
        base = min(0xffff, base + term) if same else (base - term)
        if base < 1: base = 1
    for att in (int(LVL[min(127, max(0, vel))]),
                int(LVL[min(127, max(0, zone_lvl))]),
                int(LVL[min(127, max(0, tone_lvl))])):
        base -= att
        if base < 1: base = 1
    return base

def partial_pitch_offset(raw):
    """Per-partial pitch trim, in semitones, from the static block:
       +0x10 key transpose (0x40 = neutral, whole semitones)
       +0x11 coarse tune   (0x40 = neutral, (v-0x40)*10 milli-semitones per partial_compute_pitch)
       Returned as an offset to ADD to the sample's native pitch (so a higher value plays lower)."""
    return (0x40 - int(raw[0x10])) - (int(raw[0x11]) - 0x40) / 100.0

def amp_of(level16):
    """g_amp_curve: 16-bit log level -> linear gain [0,1]  (hi[l>>8]*lo[l&0xff]>>16)."""
    l = np.clip(level16, 0, 0xffff).astype(np.int64)
    return ((AH[l >> 8] * AL[l & 0xff]) >> 16).astype(np.float64) / 65535.0

# ---- envelope segment-rate machine (reversed from tva_compute_env_rates) ----
RATE_CURVE = np.frombuffer(open(os.path.join(_T, "curve_segrate_2900.bin"), "rb").read(), np.uint16)  # g_rate_curve: rate byte -> segment ms
ROUT   = np.frombuffer(open(os.path.join(_T, "curve_rateout_3060.bin"), "rb").read(), np.uint16)      # g_env_rate_out
SCURVE = np.frombuffer(open(os.path.join(_T, "curve_scale_28e8.bin"),   "rb").read(), np.uint8)       # g_env_scale_curve
KF0    = np.frombuffer(open(os.path.join(_T, "kf_tvarate0_023a0.bin"),  "rb").read(), np.uint8)       # g_kf_tvarate0

def _s16(x): return x - 0x10000 if x >= 0x8000 else x
def env_rate_scale(base, mod):
    """FUN_1800607e0: (base rate byte, 0x40-neutral modifier) -> 8.8 rate multiplier (0x100 = 1.0)."""
    base = int(base); mod = int(mod)
    u = mod - 0x40
    if u == 0: return 0x100
    if u < 0:
        c = base & 0xff; c = c - 256 if c >= 128 else c
        u = -u; base = (-c) & 0xff
        if base == 0: base = 0xff
    base = _s16((base - 0x80) & 0xffff)
    if base == 0: return 0x100
    sc = int(SCURVE[u])
    if base < 0: return int(ROUT[(0x80 - ((base * sc * -2) >> 8)) & 0x1ff])
    return int(ROUT[(((base * sc * 2) >> 8) + 0x80) & 0x1ff])

def _sh8(v):  # (short)(v<<2) >> 8   (signed 16-bit)
    w = (v << 2) & 0xffff
    return (w - 0x10000 if w >= 0x8000 else w) >> 8
def env_level_scale(a, b):
    """FUN_180060880: (level byte a, 0x40-neutral param b) -> 8.8 multiplier (0x100 = 1.0)."""
    a = int(a); b = int(b)
    s2 = 0x40 - a
    if s2 == 0: return 0x100
    s1 = b - 0x40
    if s1 == 0: return 0x100
    if s2 < 0:
        if s1 >= 0:
            return int(ROUT[(0x80 - _sh8(-(int(SCURVE[(s1 * 2) & 0xff]) * s2))) & 0x1ff])
        v = -(int(SCURVE[(-s1 * 2) & 0xff]) * s2)
    else:
        if s1 < 0:
            return int(ROUT[(0x80 - _sh8(int(SCURVE[(-s1 * 2) & 0xff]) * s2)) & 0x1ff])
        v = int(SCURVE[(s1 * 2) & 0xff]) * s2
    return int(ROUT[(_sh8(v) + 0x80) & 0x1ff])

def _seg_ms(rate_byte, rate_mult, vel_mult=0x100, bias=0):
    """segment_time_ms = (vel_mult * min(0xffff, (rate_mult * g_rate_curve[idx])>>8)) >> 8.

       idx = clamp((rate_byte & 0x7f) + bias, 0x7f), and idx < 0 gives a zero-length segment.
       `bias` is the part-level env-rate offset ((pch[0x457+k] + pch[0x3e8+k])*2 - 0x100); it is 0
       when both patch rate params are the neutral 0x40, the only case we render today.
       Bit 7 of the byte is NOT part of the rate -- it is the segment shape flag, see _seg_curve."""
    idx = (rate_byte & 0x7f) + bias
    if idx < 0: return 0.0
    T = int(RATE_CURVE[min(idx, 0x7f)])
    if T < 9: return 0.0
    u = min(0xffff, (rate_mult * T) >> 8)
    return float((vel_mult * u) >> 8)

# g_env_shape (env_ramp_segment): fast-approach interpolation curve, indexed by (255 - phase_hi).
SHAPE = np.frombuffer(open(os.path.join(_T, "env_shape_7a90.bin"), "rb").read()[:0x200], np.uint16).astype(np.float64) / 65535.0
def _seg_curve(u, g_start, g_target, linear=False):
    """One segment's gain over normalized time u (0..1), exactly as `env_ramp_segment` @180083a70.

       The segment's rate byte carries a SHAPE FLAG in bit 7, extracted before the 0x7f table mask:
           shape_word = ((int16)(int8)rate_byte) >> 15 & 0x4000
       and the ramp handler branches on it:
           0x4000 -> LINEAR      out = start + ((target-start) * phase) >> 16
           0      -> EXPONENTIAL out = start + (~f * (target-start)) >> 16, f interpolated from
                                 g_env_shape[~(phase>>8)]  -- the fast-approach curve
       TVF and pitch envelopes hardcode 0x4000 (always linear); the TVA is the only envelope where
       the shape is data-driven. `g_env_shape` is ~99.6% of the way to target by half the segment,
       so rendering a linear segment with it collapses the segment into a near-instant jump."""
    u = np.clip(u, 0, 1)
    if linear:
        return g_start + (g_target - g_start) * u
    # g_env_shape lookup, INTERPOLATED between adjacent entries by the low byte of the 16-bit phase.
    # env_ramp_segment @180083a70 interpolates the fast-approach curve; a bare 256-level lookup steps
    # 256x across the segment, and on a long decay those ~4% steps are amplitude discontinuities that
    # inject a broadband noise floor -- the audible "graininess" in a sustain (marimba, etc.). The
    # engine's per-sample interpolation makes the decay a smooth ramp with no step edges. Fixing this
    # lifts the sustain SNR from ~80 dB (stepped) to ~96 dB, matching the DLL's own render.
    ph  = (u * 0xffff).astype(np.int64)
    idx = ph >> 8                                          # high byte -> shape entry (0..255)
    lo  = (ph & 0xff) / 256.0                              # low byte -> interpolation fraction
    i0  = np.clip(255 - idx, 0, 255)
    i1  = np.clip(254 - idx, 0, 255)                       # next entry as u increases (~phase decreases)
    frac = SHAPE[i0] + (SHAPE[i1] - SHAPE[i0]) * lo        # 1.0 at u=0 -> 0.0 at u=1, dropping fast
    return g_target + (g_start - g_target) * frac

# ---------------------------------------------------------------- codec (vectorized)
_wave_cache = {}
def decode_wave(c):
    """Decode one wave to (float32 buffer, loopStart_idx, dataEnd_idx, mode, shifted_deltas).

       `mode` is the SAMPLER VARIANT the engine would pick, derived from the descriptor flags the
       way the engine does it. `wavedesc_decode` @18005ec90 packs them into wave_ctrl:
           wave_ctrl = ((flags&4) + ((flags&1)+8)*8) * 0x200 + region
       and voice start (@180089xxx) unpacks them to choose the sampler:
           u33 = (wave_ctrl>>2 & 0x400) | (wave_ctrl & 0x800);  u34 = u33>>10
           u33==0 -> run_flags 0x02   u34==1 -> 0x04   u34==2 -> 0x22   u34==3 -> 0x24
       and `voice_render_dispatch` @18003f720 switches on run_flags&6 (+0x20 = reverse variant):
           0 -> sampler_pcm @18003f9d0     (one-shot: zeroes the predictor at the end)
           2 -> sampler_adpcm4 @18003fb80  (forward loop)
           4 -> sampler_fmt4 @18003fdd0    (BIDIRECTIONAL / ping-pong)
       So flags bit0 = bidirectional, bit2 = reverse, and **bit1 takes no part in the dispatch**.
       Our old `flags&2 => one-shot` rule was right by accident: all 649 flags==2 waves have an
       EMPTY loop region (loopPoint == dataEnd), which is what actually makes them one-shots."""
    bank = (c['region'] >> 4) & 1
    key  = (c['region'], c['loop'], c['start'], bank)
    if key in _wave_cache:
        return _wave_cache[key]
    eff  = c['region'] - 16 * bank
    base = BASE[bank] + eff * 0x100000
    aligned = c['loop'] & ~0x1f
    n = c['start'] - aligned
    if n <= 0 or n > 2_000_000:
        _wave_cache[key] = None; return None
    dbase, sbase = base + aligned, base + (aligned >> 5)
    m     = n + 1                                                # fmt4 turns around ON index n
    delta = np.frombuffer(DLL, np.int8, count=m, offset=dbase).astype(np.int64)
    scb   = np.frombuffer(DLL, np.uint8, count=(m >> 5) + 4, offset=sbase).astype(np.int64)
    pos   = np.arange(m)
    sb    = scb[pos >> 5]
    scale = np.where((pos >> 4) & 1, (sb >> 4) & 0xf, sb & 0xf)   # nibble per 16-sample block
    ds    = delta << (scale + 10)                                # one integrator step per sample
    buf   = (np.cumsum(ds[:n]).astype(np.float64) * CONST).astype(np.float32)
    loopS = max(0, c['end'] - aligned)                           # loop point (idx) = end-aligned
    if c['flags'] & 1:      mode = 'pingpong'                    # sampler_fmt4
    elif n - loopS > 0:     mode = 'loop'                        # sampler_adpcm4, real loop region
    else:                   mode = 'oneshot'                     # empty region -> stops at the end
    res = (buf, loopS, n, mode, ds)
    _wave_cache[key] = res
    return res

# g_interp_coef_table @0x181a0f210 -- the sampler's 4-tap FIR resampling kernel, 128 phase rows of
# 4 float coefficients. The samplers index it as (phase >> 9) * 0x10, i.e. the top 7 bits of the
# 16-bit fractional phase, and apply it to the rolling 4-sample window with the newest sample last.
# NOTE this region of the image needs a -0x1400 section adjustment, not the -0x1000 that .rdata uses.
# Every row sums to 1.0, and even the frac=0 row is [0.174, 0.653, 0.173, 0] -- a mild lowpass that
# is ALWAYS applied. Linear interpolation ([0,1,0,0] at frac=0) is measurably brighter: at fs/4 the
# engine kernel passes 0.638 where linear passes 0.707.
ICOEF = np.frombuffer(open(os.path.join(_T, "interp_coef_a0f210.bin"), "rb").read(),
                      np.float32).reshape(128, 4)

def _interp4(buf, p):
    """4-tap FIR resample of `buf` at fractional positions `p`, as the engine does it."""
    i0 = np.floor(p).astype(np.int64)
    idx = np.clip(((p - i0) * 128).astype(np.int64), 0, 127)
    i0 = np.clip(i0, 1, len(buf) - 3)
    c = ICOEF[idx]
    return (c[:, 0] * buf[i0 - 1] + c[:, 1] * buf[i0] +
            c[:, 2] * buf[i0 + 1] + c[:, 3] * buf[i0 + 2]).astype(np.float32)

# ---------------------------------------------------------------- LFO
# lfo_advance_waveform @180082a30, transcribed. A 16-bit phase accumulator advanced once per 100 Hz
# control tick by `inc`, so f = inc * 100 / 65536 Hz (that identity is what independently confirmed
# the 100 Hz tick: g_lfo_rate_tbl[127] = 13107 -> exactly 20.0 Hz, and [1] = 65 -> 0.0992 Hz).
# Output is a signed 16-bit swing, roughly +/-32640.
LFOWAVE = np.frombuffer(open(os.path.join(_T, "lfo_wave_1740.bin"), "rb").read(), np.uint8)     # half-sine, 128 used
LFORATE = np.frombuffer(open(os.path.join(_T, "lfo_rate_2790.bin"), "rb").read(), np.uint16)    # rate byte -> phase inc
LFOCENT = np.frombuffer(open(os.path.join(_T, "lfo_cents_2690.bin"), "rb").read(), np.uint16)   # depth byte -> cents
LFOBANK = np.frombuffer(open(os.path.join(_T, "lfo_wavebank_a17f0.bin"), "rb").read(),
                        "<i2").reshape(24, 0x81)                                                # waveforms 8..0x1f

def _s16(v):
    v &= 0xffff
    return v - 0x10000 if v >= 0x8000 else v

def lfo_wave(phase, sel=0):
    """Evaluate the LFO at 16-bit `phase` for waveform `sel`. Deterministic shapes only:
       0 sine, 4 square, 5 saw, 6 triangle, 7 triangle(offset), 8..0x1f interpolated wavetables.
       Selectors 1/2/3 are the random ones (S&H and two slew-limited variants) -- they are driven by
       prng_lfsr, a Galois LFSR, so they need the engine's RNG state and are NOT modelled here."""
    p = int(phase) & 0xffff
    if sel == 0:                                    # half-sine table, mirrored for the second half
        v = int(LFOWAVE[(p >> 8) & 0x7f])
        return v * -0x80 if p > 0x8000 else v * 0x80
    if sel == 4: return _s16(((p - 0x10000 if p >= 0x8000 else p) >> 15 & 2) + 0x7fff)
    if sel == 5: return _s16(p - 0x8000)
    if sel == 6:
        q = p >> 14
        if q == 1: return _s16(~(p * 2))
        if q == 3: return _s16(p * 2 + 1)
        return _s16(((-p) if q == 2 else p) * 2)
    if sel == 7:
        q = p >> 14
        if q == 1: return _s16(~(p * 2) + 0x8001)
        if q == 3: return _s16(p * 2 - 0x7ffe)
        return _s16(((-p) if q == 2 else p) * 2 - 0x7fff)
    if 8 <= sel < 0x20:                             # linear-interpolated wavetable
        w = LFOBANK[sel - 8]; i = p >> 9; frac = (p >> 1) & 0xff
        return _s16(((int(w[i + 1]) - int(w[i])) * frac >> 8) + int(w[i]))
    return 0                                        # 1/2/3 (random) and anything else: no modulation

def lfo_series(inc, nticks, sel=0, phase0=0):
    """The LFO's output at each 100 Hz control tick, as an (nticks,) array in [-1, 1]."""
    ph = int(phase0) & 0xffff
    out = np.empty(nticks, np.float64)
    for i in range(nticks):
        ph = (ph + int(inc)) & 0xffff
        out[i] = lfo_wave(ph, sel) / 32640.0
    return out

LFOWMAP = np.frombuffer(open(os.path.join(_T, "lfo_wavemap_87ae0.bin"), "rb").read(), np.uint8)
LFODLY  = np.frombuffer(open(os.path.join(_T, "lfo_delay_a2590.bin"),  "rb").read(), np.uint16)

# There are TWO LFO engines per note, and a partial is modulated by BOTH:
#   LFO1  lfo_update @180081b90        -- tone-COMMON, params in the tone header 0x0e..0x15,
#         depths per-partial at block +0x15 (pitch, s8 index into g_lfo_cents_tbl), +0x34, +0x56.
#         Its rate field is an INDEX into g_lfo_rate_tbl; its delay field indexes g_lfo_delay_tbl.
#   LFO2  FUN_1800823b0 @1800823b0     -- per-PARTIAL, params in the 0x6e block 0x06..0x0d,
#         depths at +0x16 (pitch, direct), +0x36, +0x58.
#         Its rate and delay fields are used RAW as per-tick increments. That asymmetry is real:
#         verified live, LFO1 rate 43 -> increment 2817 while LFO2 rate 917 -> increment 917.
# Waveform byte: bits 0-4 index (through g_lfo_wavemap), bit 5 mod-source, bits 6-7 initial phase.
# A delay field that yields rate 0 (e.g. the stored -1) means the delay accumulator never
# saturates, so that LFO stays silent forever -- that is how Piano and Sweep Pad switch LFO2 off.
def lfo_config(tone_no, pi):
    """Both LFOs' static configuration for one partial."""
    c = D.tone_b[tone_no*0x100 : tone_no*0x100+0x24]
    b = P._block(tone_no, pi)
    def u16(x, o): return x[o] | (x[o+1] << 8)
    def s16(x, o):
        v = u16(x, o); return v - 0x10000 if v >= 0x8000 else v
    d1 = s16(c, 0x12)
    p1 = int(b[0x15]); p1 = p1 - 0x100 if p1 >= 0x80 else p1          # signed index
    cents1 = -int(LFOCENT[p1 & 0x7f]) if p1 < 0 else int(LFOCENT[min(0x7f, p1)])
    return dict(
        lfo1=dict(wave=int(LFOWMAP[c[0x0e] & 0x1f]), phase0=(c[0x0e] & 0xc0) << 8,
                  inc=int(LFORATE[min(0x7f, max(0, u16(c, 0x10)))]),
                  delay=int(LFODLY[min(0x7f, max(0, d1))]),
                  fade=u16(c, 0x14),
                  # C's `/2` on a signed short TRUNCATES toward zero; Python's // floors.
                  # -179 must give -89, not -90 (this was a real 1-unit mismatch vs the engine).
                  pitch=cents1, tvf=int(s16(b, 0x34) / 2), tva=s16(b, 0x56)),
        lfo2=dict(wave=int(LFOWMAP[b[0x06] & 0x1f]), phase0=(b[0x06] & 0xc0) << 8,
                  inc=u16(b, 0x08),
                  # LFO2's delay field is a RAW per-tick increment, but <= 0 means INSTANT
                  # (0xffff), not "never". Measured: a stored -1 yields delay_rate 65535 on both
                  # Jetplane and Piano. Treating <=0 as 0 silently disabled LFO2 on every such
                  # patch -- MutedTrumpet's field is 6553 so the bit-exact test never caught it.
                  delay=(s16(b, 0x0a) if s16(b, 0x0a) > 0 else 0xffff), fade=u16(b, 0x0c),
                  pitch=s16(b, 0x16), tvf=int(s16(b, 0x36) / 2), tva=s16(b, 0x58)))

# Depth clamps and the rounding variant per destination. TVA goes through FUN_1800828f0
# (ceiling); TVF and pitch through FUN_180082940 (round to nearest). Both are the same
# sign-magnitude fixed-point multiply otherwise -- only the rounding constant differs.
LFO_DEST = {'tva': (0x7f00, 0xffff), 'tvf': (0x1800, 0x8000), 'pitch': (6000, 0x8000)}

def _lfo_run(cfg, nticks, dest='pitch'):
    """One LFO's output per control tick for one destination.

       pitch is in MILLI-SEMITONES (1000 = 1 semitone): the pitch accumulator FUN_1800830e0
       clamps to 0x1f018 = 127000 = 127 semitones x 1000, which is what fixes the unit.
       tvf is in the same 15-bit domain as the runtime cutoff (added by FUN_180084350).
       tva is a fraction of 0x7f00 applied multiplicatively to the voice gain (FUN_180060390)."""
    clamp, rnd = LFO_DEST[dest]
    ph, dacc, facc = int(cfg['phase0']) & 0xffff, 0, 0
    dr, fr, dep, sel, inc = cfg['delay'], cfg['fade'], cfg[dest], cfg['wave'], cfg['inc']
    out = np.zeros(nticks, np.float64)
    if dep == 0 or inc == 0: return out
    dep = max(-clamp, min(clamp, dep))
    for i in range(nticks):
        ph = (ph + inc) & 0xffff
        if dacc < 0xffff:                       # delay phase: LFO runs but is not applied
            if dr == 0: continue                # never completes -> this LFO is disabled
            dacc = min(0xffff, dacc + dr)
            if dacc < 0xffff: continue
        if facc < 0xffff: facc = min(0xffff, facc + fr)
        eff = dep if facc == 0xffff else (abs(dep) * facc >> 16) * (1 if dep >= 0 else -1)
        w = lfo_wave(ph, sel)
        m = (abs(w) * abs(eff) * 2 + rnd) >> 16          # sign-magnitude fixed-point multiply
        out[i] = m if (w >= 0) == (eff >= 0) else -m
    return out

def lfo_mod_ms(tone_no, pi, nticks, dest):
    """Combined LFO1+LFO2 modulation for one destination, per control tick, engine-aligned."""
    cfg = lfo_config(tone_no, pi)
    if nticks <= 0: return np.zeros(0)
    tot = _lfo_run(cfg['lfo1'], nticks - 1, dest) + _lfo_run(cfg['lfo2'], nticks - 1, dest)
    return np.concatenate([[0.0], tot])

def lfo_pitch_ms(tone_no, pi, nticks):
    """Total LFO pitch modulation for a partial, milli-semitones per 100 Hz control tick.

       Validated BIT-EXACT against the engine: `scdec lfotrace 59 60` dumps the live LFO objects
       (pool at module+0x5c340, stride 0xa8) every control tick, and both LFOs' mod_pitch match
       ours on 199/199 ticks with zero difference. The leading 0 is the engine's own first tick --
       the object is created after that tick's control update, so nothing is applied yet."""
    return lfo_mod_ms(tone_no, pi, nticks, 'pitch')

# ---------------------------------------------------------------- mod wheel (CC1) -> LFO1 pitch
def mod_wheel_depth_ms(cc1, D=10, S=0):
    """GS mod-wheel (CC1) -> added LFO1 pitch depth in milli-semitones. lfo_update @180081b90
       (line 71847): raw = (D*cc1)//4 + S  (C >>2 truncates toward 0), clamp +/-4032, then
       *2*48762 >>16 (gain ~1.48811). D = part[0x425] GS 'MOD LFO1 PITCH DEPTH' (default 0x0A);
       S = part[0x3ae] (default 0). Full scale D=0x7f, cc1=127 -> 6000 = +/-6 semitones (GS spec).
       This is SUMMED with the tone's pitch depth IN CENTS after the LFOCENT table
       (lfo_apply_depth @1800820f0), then the total is clamped to +/-6000."""
    raw = int(D) * int(cc1) // 4 + int(S)
    raw = max(-4032, min(4032, raw))
    a = (abs(raw) * 2 * 48762) >> 16
    return a if raw >= 0 else -a

def _lfo1_pitch_series(cfg1, nticks, mod_ms):
    """LFO1 pitch output per control tick (milli-semitones) with a per-tick mod-wheel depth added
       AFTER the fade: depth(i) = clip(faded_tone_cents + mod_ms[i], +/-6000), matching
       lfo_apply_depth. Mirrors _lfo_run's phase/delay/fade machinery for the 'pitch' dest."""
    clamp, rnd = LFO_DEST['pitch']                       # (6000, 0x8000)
    ph, dacc, facc = int(cfg1['phase0']) & 0xffff, 0, 0
    dr, fr, tone, sel, inc = cfg1['delay'], cfg1['fade'], cfg1['pitch'], cfg1['wave'], cfg1['inc']
    out = np.zeros(nticks, np.float64)
    if inc == 0: return out
    tone = max(-clamp, min(clamp, tone))
    last = len(mod_ms) - 1
    for i in range(nticks):
        ph = (ph + inc) & 0xffff
        if dacc < 0xffff:
            if dr == 0: continue
            dacc = min(0xffff, dacc + dr)
            if dacc < 0xffff: continue
        if facc < 0xffff: facc = min(0xffff, facc + fr)
        ft = tone if facc == 0xffff else (abs(tone) * facc >> 16) * (1 if tone >= 0 else -1)
        eff = ft + int(mod_ms[i] if i <= last else mod_ms[last])
        eff = max(-clamp, min(clamp, eff))
        w = lfo_wave(ph, sel)
        m = (abs(w) * abs(eff) * 2 + rnd) >> 16
        out[i] = m if (w >= 0) == (eff >= 0) else -m
    return out

def lfo_pitch_ms_mod(tone_no, pi, nticks, mod_ms):
    """LFO1 (with per-tick mod-wheel depth) + LFO2 pitch modulation, per control tick.
       mod_ms = per-tick mod-wheel depth in milli-st (from mod_wheel_depth_ms(cc1(t)))."""
    cfg = lfo_config(tone_no, pi)
    if nticks <= 0: return np.zeros(0)
    l1 = _lfo1_pitch_series(cfg['lfo1'], nticks - 1, mod_ms)
    l2 = _lfo_run(cfg['lfo2'], nticks - 1, 'pitch')
    return np.concatenate([[0.0], l1 + l2])

def _lfo_at_samples_pitch_mod(tone_no, pi, nsamp, mod_ms_tick):
    """Per-sample LFO pitch modulation (ms) including the mod wheel, held at control rate."""
    nt = int(np.ceil(nsamp / 320.0)) + 1
    ms = lfo_pitch_ms_mod(tone_no, pi, nt, mod_ms_tick)
    if not np.any(ms): return 0.0
    per = np.repeat(ms, 320)[:nsamp]
    if len(per) < nsamp: per = np.concatenate([per, np.full(nsamp - len(per), per[-1])])
    return per

# ---------------------------------------------------------------- pitch envelope
# partial_compute_pitch_env @180060... + partial_apply_pitch_env_rates. Unlike the TVA/TVF
# envelopes, the pitch envelope's output IS the pitch (absolute milli-semitones, 1000 = 1
# semitone) rather than an offset -- every stage target is written as `base +/- delta`, where
# `base` is the note's own pitch. We therefore work in offsets-from-base, which cancels the
# whole base chain (key follow, coarse tune, sample root) and leaves a pure ratio multiplier.
#
#   delta(bias) = (g_pitch_bias_curve[|bias|] * depth) >> 7,  negated when the bias is negative
#   bias        = (s8)block[0x1b + i] - 0x40    for i = 0..4
#   start level = delta(b0);  segment targets = delta(b1), delta(b2), delta(b3), 0
#                             (the 4th target is the UNBIASED base, i.e. offset 0)
#   release     = delta(b4)
#   segment time_ms = ((KF * g_rate_curve[rate & 0x7f]) >> 8) * VS >> 8, zero if the rate byte
#                     is 0 or the curve entry < 9;  KF0 for stages 0-3, KF1 for the release.
# Gated off entirely when block[0x18] == 0 (the engine zeroes voice+0x7a, its enable flag);
# Piano and MutedTrumpet are both 0, which is why nothing before Jetplane exposed this.
PBIAS = np.frombuffer(open(os.path.join(_T, "curve_pitchbias_2890.bin"), "rb").read(), np.uint8)
PDVS  = np.frombuffer(open(os.path.join(_T, "curve_pitchdepthvs_28d0.bin"), "rb").read(), np.uint16)
KFP0  = np.frombuffer(open(os.path.join(_T, "kf_pitchrate0_01f20.bin"), "rb").read(), np.uint8)
KFP1  = np.frombuffer(open(os.path.join(_T, "kf_pitchrate1_01aa0.bin"), "rb").read(), np.uint8)
_PENS = np.frombuffer(_PEN[:0x80 * 2], "<i2")            # DAT_1819a2578, depth vel-sens slope

def pitch_env_offsets(raw, key, vel):
    """(start_offset, [4 segment targets], release_target, [4 times ms], release_ms) or None."""
    dep = raw[0x18] | (raw[0x19] << 8)
    if dep == 0: return None                              # envelope disabled
    v = int(raw[0x2b]) - 0x40                             # depth velocity-sensitivity
    if v == 0:
        D = dep
    else:
        vv = min(127, max(0, vel))
        if v < 0: v = -v; vv = (-vv) & 0x7f
        D = ((int(_PENS[v]) * vv + int(PDVS[v])) * dep + 0x8000) >> 16
    def delta(bs):
        if bs == 0: return 0
        d = (int(PBIAS[min(64, abs(bs))]) * D) >> 7
        return -d if bs < 0 else d
    b = [((int(raw[0x1b + i]) - 0x100 if raw[0x1b + i] >= 0x80 else int(raw[0x1b + i])) - 0x40)
         for i in range(5)]
    kf0 = env_rate_scale((int(KFP0[raw[0x27] * 0x80 + (key & 0x7f)]) - 0x80) & 0xff, raw[0x29])
    kf1 = env_rate_scale((int(KFP1[raw[0x27] * 0x80 + (key & 0x7f)]) - 0x80) & 0xff, raw[0x2a])
    vs  = env_level_scale(min(127, max(0, vel)), raw[0x2c])
    def T(rb, kf):
        r = int(rb) & 0x7f; S = int(RATE_CURVE[r])
        return 0 if (r == 0 or S < 9) else ((((kf * S) >> 8) & 0xffff) * vs >> 8)
    return (delta(b[0]), [delta(b[1]), delta(b[2]), delta(b[3]), 0], delta(b[4]),
            [T(raw[0x20 + i], kf0) for i in range(4)], T(raw[0x24], kf1))

def _seg_rate_word(T_ms):
    """env_ramp_segment's rate word: `T < 11 ? 0xffff : 0xa0000/T`. 0xffff wraps the 16-bit
       phase in exactly one control tick, so a 'zero-time' segment still takes one tick."""
    return 0xffff if T_ms < 11 else min(0xffff, 0xa0000 // int(T_ms))

def pitch_env_ticks(raw, key, vel, hold, nticks):
    """Per-CONTROL-TICK pitch offset in milli-semitones, running the engine's own segment machine.

       The value is stepwise (held for 320 samples), and tick i is the state AFTER that tick's
       update -- verified on Mellow Gt., whose four one-tick segments read 0, +76, +153, 0 on
       consecutive ticks, and on Jetplane, whose 17.6 s segment reads start+9 on tick 0."""
    p = pitch_env_offsets(raw, key, vel)
    if p is None: return None
    start, targets, rel, times, rel_ms = p
    if start == 0 and not any(targets) and rel == 0: return None
    segs = [(targets[i], _seg_rate_word(times[i])) for i in range(4)]
    out = np.zeros(nticks, np.float64)
    seg_start, level, si, phase = float(start), float(start), 0, 0
    off_tick = int(hold * 100)                                   # note-off, in control ticks
    released = False
    for i in range(nticks):
        if i >= off_tick and not released:                       # splice the release segment
            released, seg_start, si, phase = True, level, -1, 0
            tgt, rate = float(rel), _seg_rate_word(rel_ms)
        elif si < 0:
            tgt, rate = float(rel), _seg_rate_word(rel_ms)
        elif si < 4:
            tgt, rate = float(segs[si][0]), segs[si][1]
        else:
            out[i] = level; continue                             # envelope finished, hold
        phase += rate
        # Completion is `phase >= 0xffff`, not `> 0xffff`, and the next segment starts FRESH
        # (no phase carry). Both follow from the engine: a rate-0xffff segment lands on its
        # target in exactly ONE tick (Nylon's tick 0 reads +43 exactly, its segment-0 target),
        # and the following 88 ms segment then ramps over 9 ticks starting from there.
        if phase >= 0xffff:                                      # segment complete
            level, seg_start, phase = tgt, tgt, 0
            if si >= 0: si += 1
        else:
            level = seg_start + (tgt - seg_start) * (phase / 65536.0)
        out[i] = level
    return out

KFPITCH = np.frombuffer(open(os.path.join(_T, "kf_pitch_01b20.bin"), "rb").read(), "<i2")  # 8 rows x 0x80
# DAT_1800935c0 -- pitch KEY-FOLLOW lookup used by multisample_key_zone. Indexed by block[0x13]-0x40
# (0..10). With the *2 in the formula, kf_scale = LUT[i]*2/65536 = i*0.1, i.e. block[0x13] gives a
# clean 0%(0x40) / 10% / 20%(seashore 0x42) / ... / 90% / 100%(0x4a, special-cased exact) key-follow.
KF_LUT = np.array([0,3276,6553,9830,13107,16383,19660,22937,26214,29491,32767], np.int64)

def keyfollow_key(raw, note, keycenter=0x3c):
    """Effective pitch key + fractional crossfade weight from block[0x13] PITCH KEY-FOLLOW, exactly as
       multisample_key_zone @180003210. block[0x13]=0x4a is full follow (key=note, weight 0 -- the
       common case, so every previously-validated voice is unchanged); 0x40 is no follow (key=center);
       between, the note's distance from the key-center is scaled by KF_LUT before it drives pitch --
       so e.g. Seashore (0x42, 20%) plays note 33 as key ~55 instead of 33, keeping its noise bright
       instead of an octaves-too-low rumble."""
    transpose = int(raw[0x10]) - 0x40
    kfp = int(raw[0x13]) - 0x40
    if kfp == 10 or kfp < 0:                      # 0x4a full follow (and any <0x40): key = note
        return note + transpose, 0
    if kfp == 0:                                  # 0x40: no follow -> key = key-center
        return keycenter + transpose, 0
    d = note - keycenter
    prod = int(KF_LUT[min(kfp, 10)]) * (abs(d) * 2)
    hi, lo = prod >> 16, prod & 0xffff
    if lo < 65000:
        w = (lo * 999) // 65000
        if d >= 0: weight, ks = w, hi
        else:      weight, ks = 1000 - w, ~hi     # ~hi == -hi-1
    else:
        weight = 0
        ks = (hi + 1) if d >= 0 else ~hi
    return keycenter + ks + transpose, weight

def base_pitch_ms(raw, note, keycenter=0x3c):
    """The note's ABSOLUTE base pitch in milli-semitones, per partial_compute_pitch @18005fc20.

         key,weight = keyfollow_key(block[0x13], note, key-center)   pitch key-follow (multisample_key_zone)
         base       = key*1000 + weight + g_kf_pitch[row][key] + (block[0x11] - 0x40)*10
         row        = (block[0x13] - 0x40) >> 2

       Verified against the engine's own voice+0x6c on 13 traced voices: exact for Jetplane
       (base 24000 and 84000, from transposes of -36 and +24 semitones) and -11 elsewhere,
       which is precisely g_kf_pitch row 2 at key 60 -- Piano's row. Those are full-follow (0x13=0x4a)
       so keyfollow_key returns key=note+transpose, weight 0 -- identical to the old code.

       This matters beyond tuning: the engine CLAMPS the absolute pitch at 0, and Jetplane's
       partial 0 sits exactly on that clamp (base 24000 with a -24000 envelope start). Working
       in offsets-from-base, as the first implementation did, cannot express that."""
    key, weight = keyfollow_key(raw, note, keycenter)
    key = max(0, min(0x7f, key))
    row = max(0, min(7, (int(raw[0x13]) - 0x40) >> 2))
    return key * 1000 + weight + int(KFPITCH[row * 0x80 + key]) + (int(raw[0x11]) - 0x40) * 10

def bend_offset_ms(bend=8192, bend_range=2):
    """Pitch-bend offset in milli-semitones. bend is the 14-bit wheel value (8192 = centre); the
       offset is (bend - 8192)/8192 * range semitones, range from RPN 00/00 (GM default 2).
       Measured from the engine's phase increment: default range = +/-2 st, range 12 = +/-12 st,
       linear in the wheel value throughout. Added into the absolute pitch (part+0x448 path,
       @72896) alongside the envelope, so it composes with the clamp."""
    return (int(bend) - 8192) / 8192.0 * bend_range * 1000.0

def pitch_ratio_abs(raw, c, note, vel, hold, tail, nsamp, lfo_ms=None, pitch_add_ms=0.0, keycenter=0x3c):
    """Per-sample playback ratio: absolute pitch (base + envelope + LFO, clamped) against the
       sample's own root. Replaces the old `2^((note-native)/12) * modulation`, which ignored
       the partial transpose/coarse tune entirely and could not apply the engine's pitch clamp.
       `keycenter` feeds the pitch key-follow (reduced-follow patches like the SFX Seashore)."""
    base = base_pitch_ms(raw, note, keycenter)
    off = pitch_env_ticks(raw, max(0, min(0x7f, note)), vel, hold,
                          int(np.ceil(nsamp / 320.0)) + 1)
    p = np.full(nsamp, float(base))
    if off is not None:
        per = np.repeat(off, 320)[:nsamp]
        if len(per) < nsamp: per = np.concatenate([per, np.full(nsamp - len(per), per[-1])])
        p = p + per
    if lfo_ms is not None: p = p + lfo_ms
    # pitch bend + part tune (part+0x448/+0x3ba). Scalar (one static value) OR a per-sample array,
    # which is how the sequencer feeds a bend/tune that moves while the note sounds.
    if np.ndim(pitch_add_ms):
        pa = np.asarray(pitch_add_ms, float)
        if len(pa) < nsamp: pa = np.concatenate([pa, np.full(nsamp - len(pa), pa[-1])])
        p = p + pa[:nsamp]
    elif pitch_add_ms:
        p = p + pitch_add_ms
    p = np.clip(p, 0.0, float(0x1f018))                  # the engine's own bounds
    native_ms = c['root'] * 1000 + 1024 - c['fine']      # voice+0x1fc, the sample's root pitch
    return 2.0 ** ((p - native_ms) / 12000.0)

def pitch_env_ratio(raw, key, vel, hold, tail, nsamp):
    """Per-sample pitch RATIO multiplier from the pitch envelope (None when disabled)."""
    nt = int(np.ceil(nsamp / 320.0)) + 1
    off = pitch_env_ticks(raw, key, vel, hold, nt)
    if off is None: return None
    per = np.repeat(off, 320)[:nsamp]
    if len(per) < nsamp: per = np.concatenate([per, np.full(nsamp - len(per), per[-1])])
    return 2.0 ** (per / 12000.0)

def _lfo_at_samples(tone_no, pi, nsamp, dest):
    """One destination's LFO modulation held at control rate and expanded to `nsamp` samples.
       Returns a scalar 0.0 when the patch has no modulation for this destination, so callers
       can add it unconditionally without allocating."""
    nt = int(np.ceil(nsamp / 320.0)) + 1
    ms = lfo_mod_ms(tone_no, pi, nt, dest)
    if not np.any(ms): return 0.0
    per = np.repeat(ms, 320)[:nsamp]
    if len(per) < nsamp: per = np.concatenate([per, np.full(nsamp - len(per), per[-1])])
    return per

def lfo_pitch_ratio(tone_no, pi, nsamp):
    """Per-SAMPLE pitch ratio multiplier from the LFOs (1.0 when there is no modulation)."""
    nt = int(np.ceil(nsamp / 320.0)) + 1                       # 320 samples = one control tick
    ms = lfo_pitch_ms(tone_no, pi, nt)
    if not np.any(ms): return None
    per = np.repeat(ms, 320)[:nsamp]                            # control-rate steps, as the engine
    if len(per) < nsamp: per = np.concatenate([per, np.full(nsamp - len(per), per[-1])])
    return 2.0 ** (per / 12000.0)

# ---------------------------------------------------------------- pitched loop playback
def play_wave(c, note, nsamp, ratio=None):
    """Resample a wave to `note` (root-relative) for nsamp output samples, honoring the loop.
       `ratio` overrides the pitch ratio directly (drums play at natural rate x coarse offset).
       `ratio` may be a SCALAR or a per-sample array -- the array form is how vibrato/pitch-envelope
       modulation is applied, by integrating the instantaneous rate into the read position."""
    dec = decode_wave(c)
    if dec is None: return np.zeros(nsamp, np.float32)
    buf, loopS, loopE, mode, ds = dec
    if ratio is None:
        # native sample pitch (semitones) = root*1000 - fine + 1024  (partial_compute_pitch, milli-semitone)
        native = c['root'] + (1024 - c['fine']) / 1000.0
        ratio = 2.0 ** ((note - native) / 12.0)         # semitone pitch ratio vs sample native pitch
    if np.ndim(ratio) == 0:
        p = np.arange(nsamp, dtype=np.float64) * ratio   # ideal read positions
    else:
        r = np.asarray(ratio, np.float64)[:nsamp]
        if len(r) < nsamp: r = np.concatenate([r, np.full(nsamp - len(r), r[-1])])
        p = np.concatenate([[0.0], np.cumsum(r)[:-1]])   # integrate the instantaneous rate
    if mode == 'pingpong':
        # sampler_fmt4: walk the delta index up to dataEnd, turn around, walk back down to the loop
        # point, turn around again -- forever. The index is unchanged on the turnaround step, so that
        # sample's delta is applied twice. The predictor keeps ACCUMULATING in both directions (it
        # never subtracts), so the backward leg is the wave inverted+time-reversed, and both
        # turnarounds are continuous by construction: no seam, no phase jump.
        need = int(p[-1]) + 3 if nsamp else 1
        head = np.arange(0, loopE + 1)                        # 0 .. dataEnd
        cyc  = np.concatenate([np.arange(loopE, loopS - 1, -1),   # dataEnd .. loopPoint (turnaround)
                               np.arange(loopS, loopE + 1)])      # loopPoint .. dataEnd (turnaround)
        reps = max(1, -(-(need - len(head)) // len(cyc)) + 1)
        idx  = np.concatenate([head] + [cyc] * reps)[:need]
        bufx = (np.cumsum(ds[idx]).astype(np.float64) * CONST).astype(np.float32)
        return _interp4(bufx, p)
    looping = mode == 'loop' and loopE > loopS + 1
    if looping:
        # The engine loops in the DELTA domain: reaching the end it rewinds the delta/scale index
        # to the loop point and KEEPS the predictor (`sampler_adpcm4` @18003fb80 sets
        # *(param_1[2]+8) = *(param_1[2]+0xc) and never touches the accumulator at param_1[4];
        # `sampler_pcm` @18003f9d0 is the one-shot variant -- it zeroes the predictor instead).
        # There is no crossfade anywhere in the sampler.
        #
        # Because the predictor is a pure integrator, that carry adds a constant DC step
        # `drift = buf[loopE-1] - buf[loopS-1]` on every pass -- up to several x rms per second
        # (flute wave#806: -4.3 rms/s), so the engine can only work with a DC blocker downstream
        # that we have not located yet. Re-stitching the region absolutely, as below, IS the
        # engine's loop with that per-pass DC removed exactly: identical apart from the seam step,
        # which here is buf[loopS]-buf[loopE-1] instead of the engine's single delta d[loopS].
        # The engine's forward loop is INCLUSIVE of dataEnd. sampler_adpcm4 (@18003fb80) rewinds to
        # the loop point only when the running index == dataEnd (line 23179), and the normal advance
        # is +1 (sample_fetch_loop_wrap @18003f870), so one pass plays loopS..dataEnd =
        # (dataEnd - loopS + 1) samples and wraps AFTER dataEnd. Our old period (dataEnd - loopS)
        # dropped that last sample: inaudible on long multi-cycle loops, but on a short single-cycle
        # loop the pitch IS SR*ratio/period, so a 63->64 sample error is a real detune (Overdrive Gt
        # ms#154: a 63-sample loop came out +27 cents sharp, splitting its unison partner).
        L = loopE - loopS + 1                            # period, inclusive of dataEnd
        p = np.where(p < loopE + 1, p, loopS + np.mod(p - (loopE + 1), L))
        # The 4-tap window reaches up to i0+2, so near the loop end it must read the samples the
        # loop WRAPS to, not whatever follows the loop in the buffer.
        bufx = np.concatenate([buf[:loopE + 1], buf[loopS:loopS + 3]])
        if len(bufx) < loopE + 4:                        # loop shorter than the tap reach
            reps = -(-(loopE + 4 - len(bufx)) // max(L, 1)) + 1
            bufx = np.concatenate([buf[:loopE + 1]] + [buf[loopS:loopE + 1]] * reps)[:loopE + 4]
    else:
        p = np.minimum(p, loopE - 1)                     # one-shot: hold last sample
        bufx = buf
    out = _interp4(bufx, p)                               # engine's 4-tap FIR
    if not looping:                                       # silence past end of a one-shot
        out[p >= loopE - 1] = 0.0
    return out

# ---------------------------------------------------------------- TVA envelope (STATIC, fully reversed)
TVA_AMP_SCALE = 2.0   # the engine hands the sampler 2*amp_of(level); see AMP_SCALE note below

# Attack softening (the "smooth attack" toggle). The real engine's amplitude attack is INSTANT (a
# one-tick jump to full; measured via `scdec ampramp`), so 0.0 == faithful. A positive value ramps the
# gain up over that many ms -- a deliberate non-faithful enhancement that rounds the transient (some
# listeners prefer it on percussive patches like marimba). Default faithful; the sequencer/render_note
# take an `attack_ms` override, so a caller can opt into e.g. 3.0 for the smooth attack.
DEFAULT_ATTACK_MS = 0.0

def compute_tva_env(raw, pp, vel, hold, tail, key, zone_lvl=127, tone_lvl=127, attack_ms=0.0):
    """TVA amplitude envelope, entirely from the static 0x6e block + curve tables (no engine):
       4 segments (level block[0x5a..0x5d] via g_level_curve->g_amp; time block[0x5e..0x61] via the
       segment-rate machine) run in sequence; note-off splices a release (rate block[0x62]).

       The absolute scale is validated against the engine's OWN per-voice gain (the float at
       DAT_181a1d830 + (v&3)*0x40 + (v>>2)*4, which voice_ctrl_ramp_a writes each control tick and
       hands straight to the sampler): engine_amp == 2 * amp_of(tva_base_level(...)), exact to the
       harness's 6-decimal print on all 8 measured drum voices (max err 5.4e-5)."""
    nsamp = int((hold + tail) * SR)
    # voice+0x164: the partial's velocity-crossfaded LEVEL, not the MIDI velocity. Every consumer
    # below reads that field in the engine (@18005f0aa), so raw `vel` must not be used here.
    plv = P.partial_level(raw, vel)
    if plv is None: plv = vel
    base16 = tva_base_level(raw, plv, key, zone_lvl, tone_lvl)   # exactly as tva_compute_base_level does it
    # segment targets in the GAIN domain: amp_of(base16 - g_level_curve[stage]) -- as the engine stores them
    # A stage level whose attenuation exceeds the base is SILENCE, not the amplitude table's floor.
    # g_amp_curve_hi[0] is 4, not 0, so amp_of(0) returns 4.6e-05 and the old max(0, ...) clamp
    # left every decayed-to-nothing partial sitting at -80 dB forever. The engine's own gain word
    # reads exactly 0.000000 in that state (measured on Piano 2's second partial).
    G = []
    for i in range(4):
        lv = base16 - int(LVL[raw[0x5a + i]])
        G.append(0.0 if lv <= 0 else
                 TVA_AMP_SCALE * float(amp_of(np.array([lv]))[0]))
    b4 = env_rate_scale((int(KF0[raw[0x65] * 0x80 + key]) - 0x80) & 0xff, raw[0x67])  # main rate mult (key-follow)
    b6 = env_rate_scale((int(KF0[raw[0x66] * 0x80 + key]) - 0x80) & 0xff, raw[0x68])  # release rate mult
    b0 = env_level_scale(plv, raw[0x69])                                       # vel mult: segs 0,1
    b2 = env_level_scale(plv, raw[0x6a])                                       # vel mult: segs 2,3 + release
    st  = [_seg_ms(raw[0x5e + i], b4, b0 if i < 2 else b2) for i in range(4)]  # segment times (ms)
    rel = _seg_ms(raw[0x62], b6, b2)
    # run the 4 segments in sequence (each shaped by g_env_shape), interrupted by note-off at `hold`
    gain = np.zeros(nsamp, np.float64)
    hi = min(int(hold * SR), nsamp)
    t, g_prev = 0.0, 0.0
    for i in range(4):
        T = st[i] / 1000.0
        # Attack-from-silence floor. RESOLVED by direct measurement (`scdec ampramp`): the engine's
        # per-voice gain word jumps INSTANTLY from 0 to the full level in one control update on a fast
        # attack (marimba: 0 -> 0.6287 at the first tick, held; no ramp, no anti-zipper staircase) --
        # the only "attack" is the sample's own recorded transient. So the FAITHFUL floor is 0 ms.
        # `attack_ms` > 0 is a deliberate NON-faithful softening (the smooth-attack toggle): it ramps
        # the gain up over that many ms, rounding the transient. Default 0 = faithful/instant.
        if i == 0 and attack_ms > 0: T = max(T, attack_ms / 1000.0)
        lo = int(t * SR); hi2 = min(int((t + T) * SR), nsamp)
        if hi2 > lo and T > 0:
            gain[lo:hi2] = _seg_curve((np.arange(hi2 - lo)) / (T * SR), g_prev, G[i],
                                      linear=bool(raw[0x5e + i] & 0x80))
        g_prev, t = G[i], t + T
        if t >= hold: break
    if int(t * SR) < hi: gain[int(t * SR):hi] = g_prev       # segments done before note-off -> sustain
    off = float(gain[hi - 1]) if hi > 0 else g_prev          # gain at note-off
    ri = min(nsamp - hi, max(1, int(rel / 1000.0 * SR)))     # release segment (shaped) to silence
    if ri > 0:
        gain[hi:hi + ri] = _seg_curve(np.arange(ri) / (rel / 1000.0 * SR) if rel > 0 else np.ones(ri),
                                      off, 0.0, linear=bool(raw[0x62] & 0x80))
    gain[hi + ri:] = 0.0
    # NOTE: the gain is deliberately left SMOOTH per sample rather than stepped per control tick.
    # The engine's gain word is a control-rate value, so a stepwise model looks more faithful --
    # but it measures worse (59/72 voices within 5% vs 62/72, median error 2.74% vs 1.42%). That
    # fits the env block's anti-zipper ramp word at +0x02: the engine interpolates the gain across
    # the block rather than stepping it. Tested, not assumed.
    return gain.astype(np.float32)

# ---------------------------------------------------------------- TVF 2-pole lowpass
# FUN_180083f00's stage tables. Stages A/B/C are shared by filter types 0,1,2,4,6; only stage D
# (which produces the resonance) differs. Type 5 takes a different cutoff path (not implemented).
WARP = np.frombuffer(open(os.path.join(_T, "tvf_warp_a83d0.bin"), "rb").read(), np.uint16)  # 129, stage C
CEIL = np.frombuffer(open(os.path.join(_T, "tvf_ceil_a7ed0.bin"), "rb").read(), np.uint16)  # 128, by reso
QLP  = np.frombuffer(open(os.path.join(_T, "tvf_q_lp_a7cd0.bin"), "rb").read(), np.uint16)  # 256, type 0
QT6  = np.frombuffer(open(os.path.join(_T, "tvf_q_t6_a7fd0.bin"), "rb").read(), np.uint16)  # 256, type 6

def tvf_reso_byte(raw, part_reso=0x40, part_reso_default=0x40):
    """partial+0xEE -- the RESONANCE byte (stage A, FUN_1800845c0; duplicated in the decompile at
       partial_compute_filter). block[0x30] is the static per-partial resonance (0x40 neutral); the
       part-level terms are GS TVF Resonance (SysEx 40 1x 33 -> part+0x3e7) and its per-program
       default (part+0x456), both 0x40 when neutral. FUN_180083f00 then floors it at 4."""
    t = 2 * (0x80 - part_reso_default - part_reso) + int(raw[0x30])
    return max(4, min(0x7f, max(0, t)))

def tvf_cutoff_units(cutoff15, reso_byte):
    """15-bit cutoff sum -> partial+0xcc. Stage C (FUN_180084470): interpolate the warp table on the
       high byte, then clamp to a RESONANCE-DEPENDENT ceiling; the result is returned <<2.
       Note CEIL[0x40]*4 = 245760 = the 'fully open' value the earlier calibration measured -- that
       maximum is the neutral-resonance ceiling, not a saturation of the cutoff sum."""
    c = int(np.clip(cutoff15, 0, 0x7fff))
    hi, lo = c >> 8, c & 0xff
    v = int(WARP[hi])
    if lo:
        v += ((int(WARP[hi + 1]) - v) * lo) >> 8
    return min(v, int(CEIL[reso_byte])) << 2

def tvf_q(units, reso_byte, ftype):
    """Stage D -> partial+0xdc, normalized by the ramp seed to q = (+0xdc>>3)/16384 = +0xdc/0x20000.
       Chamberlin form, so q = 1/Q: neutral resonance 0x40 gives exactly q = 1.0 (Q = 1)."""
    if ftype == 0 and reso_byte == 0x40:      # LP-only cutoff-dependent trim at dead-neutral reso
        raw = int(QLP[(units >> 2) >> 8]) << 2
    elif ftype == 6:
        raw = int(QT6[(units >> 2) >> 8]) << 2
    else:
        raw = reso_byte << 11
    return raw / float(0x20000)

# DAT_181986420: 257 x int32 = 2^17 * 2^(i/256)  (T[256]/T[0] == 2.0 exactly). Every ramp TARGET is
# passed through this before it reaches the filter -- `voice_set_ramp_target_*` does the lookup, so
# the value the SVF sees is exponential in the linear cutoff units, not linear.
RAMPEXP = np.frombuffer(open(os.path.join(_T, "ramp_exp_986420.bin"), "rb").read(), np.int32)

def tvf_f_coef(units):
    """partial+0xcc -> the SVF's `f` coefficient, exactly as the engine builds it:
         v = (T[u]*(0x40-frac) + T[u+1]*frac) >> 6,  u = (x>>6)&0xff, frac = x&0x3f
         v >>= 0xf - ((x>>0xe)&0xf)                  -- 4-bit exponent at bits 14-17
         f  = (int16)(v>>3) / 16384
       which collapses to f = 2^(C/16384 - 15) wherever the integer path does not underflow.
       Chamberlin's f = 2*sin(pi*fc/fs), so fc = (fs/pi)*asin(f/2) -- and NO calibrated constant is
       involved anywhere. This supersedes the old fitted law Fc = 10591*2^((C-245760)/14175), which
       was ~2x too high (it claimed 10591 Hz at C=245760 where the truth is 5333 Hz) -- the direct
       cause of our long-standing excess brightness."""
    x = int(np.clip(units, 0, 0x3ffff))
    u, fr = (x >> 6) & 0xff, x & 0x3f
    v = (int(RAMPEXP[u]) * (0x40 - fr) + int(RAMPEXP[u + 1]) * fr) >> 6
    v >>= 0xf - ((x >> 0xe) & 0xf)
    return min(0x7fff, v >> 3) / 16384.0

def tvf_cutoff_hz(cutoff15, reso_byte):
    """Diagnostic only: the cutoff in Hz, derived from the SVF coefficient (no fitted constants)."""
    f = tvf_f_coef(tvf_cutoff_units(cutoff15, reso_byte))
    return float(SR / np.pi * np.arcsin(min(1.0, f / 2.0)))

# (the old fixed-cutoff `apply_tvf` is gone: its `Q = 0.707*2^((v-64)/18)` from block[0x4a] was
#  invented, and block[0x4a] is not resonance -- it scales TVF envelope depth. Use
#  apply_tvf_varying, which takes the response type from block[0x31].)

# ---------------------------------------------------------------- one note = sum of partials
# Pan table (128 bytes @ VA 0x1819a2fa1). Recovered by sweeping CC10 0..127 through the engine and
# reading the per-channel RMS: the gains come out as exact multiples of 1/127 (centre = 75/127, NOT
# the 0.707 of a constant-power law nor the 0.5 of a linear one), and that exact 128-byte run is
# present in the image. Right gain = TBL[p]/127, left = TBL[127-p]/127.
PANTBL = np.frombuffer(open(os.path.join(_T, "pan_a2fa1.bin"), "rb").read(), np.uint8)

def pan_gains(p):
    """pan byte (0x40 = centre) -> (left, right) gains.
       left = T[127-p], right = T[p-1] (right of 0 is silent). Both index 63 at centre, so pan 64
       is exactly symmetric at 75/127 -- which is what the engine measures, and is neither the
       0.707 of a constant-power law nor the 0.5 of a linear one. Reproduces the measured sweep
       with zero error on both channels."""
    p = int(np.clip(p, 0, 127))
    return float(PANTBL[127 - p]) / 127.0, (float(PANTBL[p - 1]) / 127.0 if p else 0.0)

def pan_gains_vec(p):
    """Vectorised pan_gains for a per-sample pan array (0x40 = centre) -> (gl, gr) arrays.
       Same table law as pan_gains; right-of-0 is silent (pan 0 -> gr 0)."""
    p = np.clip(np.rint(p).astype(np.int64), 0, 127)
    gl = PANTBL[127 - p].astype(np.float64) / 127.0
    gr = np.where(p > 0, PANTBL[np.clip(p - 1, 0, 127)].astype(np.float64), 0.0) / 127.0
    return gl, gr

def part_volume_scale(cc7=127, cc11=127, master=127):
    """Part-level volume multiplier from FUN_180060390 @180060390:
         u   = ((CC11 * CC7) & 0xffff) * master >> 6 & 0xffff     (CC7 vol, CC11 expression)
         vol = ((u * 0x10410) >> 16) ** 2                          (squared -> ~amplitude^2)
       Returned RELATIVE to the CC7=CC11=master=127 reference, because our per-voice gain is
       calibrated at 127 (the part volume is applied downstream of the per-voice amp word, not in
       it). Verified against the engine's audio RMS: CC7 100/64/32 -> 0.620/0.254/0.0635, exact.
       Both controllers enter symmetrically as CC7*CC11, as measured."""
    def pv(a, b, m):
        u = ((((b * a) & 0xffff) * m) >> 6) & 0xffff
        return ((u * 0x10410) >> 16) ** 2
    ref = pv(127, 127, 127)
    return pv(int(np.clip(cc7, 0, 127)), int(np.clip(cc11, 0, 127)),
              int(np.clip(master, 0, 127))) / ref

def render_note(program, note, vel, hold, tail=1.8, tone_map=4, part_pan=0x40, bank=0,
                cc7=127, cc11=127, master=127, bend=8192, bend_range=2, tune_ms=0.0,
                pv_curve=None, pitch_add_curve=None, mod_series=None, pan_curve=None,
                attack_ms=None):
    """Render one note to STEREO (N,2). Each partial is panned by its own block[0x0f]; the part-level
       pan (CC10) is applied as an offset from centre. ~894 partials are non-neutral, so the wide/
       stereo patches (St., Cho., Detuned, *w) only image correctly with this applied.
       `bend`/`bend_range` = pitch wheel (14-bit, 8192 centre) and RPN bend range; `tune_ms` = the
       part coarse/fine tune offset (part+0x3ba), a direct milli-semitone add.
       For sequencer use, the two static part-level laws can be handed in as PER-SAMPLE curves
       instead of scalars: `pv_curve` overrides the CC7xCC11xmaster volume scale, `pitch_add_curve`
       overrides bend+tune (both milli-semitones for pitch, both nsamp-length or shorter+held).
       These are how a controller that moves WHILE the note sounds becomes audible -- the engine
       re-reads them each block, so the note's timbre is latched at note-on but volume/pitch track."""
    nsamp = int((hold + tail) * SR)
    attack_ms = DEFAULT_ATTACK_MS if attack_ms is None else attack_ms   # 0 = faithful (instant); >0 = smooth-attack toggle
    pitch_add = bend_offset_ms(bend, bend_range) + tune_ms if pitch_add_curve is None else pitch_add_curve
    # program_tones() handles the indirect (0x6000+) entries -- Violin/Viola/Cello/Strings on the
    # 88Pro/8820 maps -- which our old signed-s16 read reported as unassigned. Their second slot is
    # a mono-mode alternate articulation, not a simultaneous layer, so exactly one tone sounds here.
    tones = D.program_tones(program, tone_map=tone_map, bank=bank)   # bank = CC0 MSB (variations)
    if not tones: return np.zeros((nsamp, 2), np.float32), "(unassigned)", np.zeros(nsamp, np.float32)
    mix = np.zeros((nsamp, 2), np.float32)
    mono = np.zeros(nsamp, np.float32)     # pre-pan sum of partials -- the pan-independent reverb/chorus send
    names = []
    for tno in tones:
        name, zones = D.resolve(tno, note, vel)
        names.append(name)
        for pi, w, c in zones:
            if c is None or c.get('reverse'): continue
            key = max(0, min(0x7f, note))
            raw0 = P._block(tno, pi)
            # Pitch modulation: LFO vibrato and the pitch envelope, both as ratio multipliers.
            # With a mod-wheel series, LFO1's pitch depth gets CC1's contribution added per tick.
            if mod_series is not None:
                lfo_ms = _lfo_at_samples_pitch_mod(tno, pi, nsamp, mod_series)
            else:
                lfo_ms = _lfo_at_samples(tno, pi, nsamp, 'pitch')
            pp = P.partial_params(tno, pi)
            kc = pp['keyCenter'] if pp is not None else 0x3c    # pitch key-follow centre (multisample zone)
            r = pitch_ratio_abs(raw0, c, note, vel, hold, tail, nsamp,
                                lfo_ms if np.ndim(lfo_ms) else None, pitch_add, keycenter=kc)
            sig = play_wave(c, note, nsamp, ratio=r)
            pan_base = 0x40                            # the partial's own pan (block[0x0f]); 0x40 = centre
            if pp is not None:
                raw = raw0
                cb = compute_tvf_env(raw, vel, hold, tail, key)                              # moving cutoff
                cb = cb + _lfo_at_samples(tno, pi, nsamp, 'tvf')     # FUN_180084350 ADDS the LFO
                cb = np.clip(cb, 0, 0x7fff)                          # ...then clamps, as it does
                sig = apply_tvf_varying(sig, cb, pp['tvfType'], tvf_reso_byte(raw))  # LP/HP/BP/notch
                env = compute_tva_env(raw, pp, vel, hold, tail, key,                         # static TVA env
                                      D.zone_level(pp['msamp'], key, pp['keyCenter']),
                                      D.tone_level(tno), attack_ms=attack_ms)
                # FUN_180060390 folds the TVA LFO into the voice volume as a fraction of 0x7f00:
                # vol' = vol + vol*mod/0x7f00, with the summed mod clamped to +/-0x7f00 first.
                tremolo = _lfo_at_samples(tno, pi, nsamp, 'tva')
                if np.any(tremolo):
                    env = env * (1.0 + np.clip(tremolo, -0x7f00, 0x7f00) / float(0x7f00))
                sig = sig * env
                pan_base = pp['pan']                 # [likely] how part pan combines with partial pan
            # part pan (CC10) offsets the partial pan; scalar, or a per-sample curve when CC10 moves
            # while the note sounds (the engine re-reads pan each control tick).
            if pan_curve is not None:
                pc = np.asarray(pan_curve, float)
                if len(pc) < nsamp: pc = np.concatenate([pc, np.full(nsamp - len(pc), pc[-1])])
                gl, gr = pan_gains_vec(np.clip(pan_base + (pc[:nsamp] - 0x40), 0, 127))
            else:
                gl, gr = pan_gains(pan_base + (part_pan - 0x40))
            mix[:, 0] += sig * gl
            mix[:, 1] += sig * gr
            mono += sig                         # pre-pan (pan-independent) send source
    name = names[0].rstrip(': ') if names else "(none)"
    # Partials SUM. Each partial is an independent voice and render_block @18008b1d0 dispatches
    # every active voice into the same accumulation buffer -- there is no divide-by-voice-count
    # anywhere in the mix path. This used to average (mix / len(zones)), which was an invention:
    # it silently halved every 2-partial patch relative to a 1-partial one.
    if pv_curve is not None:                       # sequencer: volume moves while the note sounds
        pvc = np.asarray(pv_curve, float)
        if len(pvc) < nsamp: pvc = np.concatenate([pvc, np.full(nsamp - len(pvc), pvc[-1])])
        mix = mix * pvc[:nsamp, None]
        mono = mono * pvc[:nsamp]                   # send is POST-fader (wet scales with volume)
    else:
        pv = part_volume_scale(cc7, cc11, master)  # CC7 volume x CC11 expression x master, part-level
        if pv != 1.0: mix = mix * pv; mono = mono * pv
    return mix, name, mono

# ---------------------------------------------------------------- drums
# Drum kit table (static, in SCCore.dll). Each kit is note-indexed parallel planes:
#   +0x000 tone# (128 x u16)   +0x100 level   +0x180 coarse pitch (60 = natural)
#   +0x200 mute/assign group   +0x280 pan     +0x300 reverb/flags
# A drum note picks its own tone; the note does NOT transpose the sample -- the +0x180
# plane supplies a coarse offset (that's how the tom pairs share one wave at different pitch).
# Kit records are a uniform 0x50C stride from DRUM_KIT_BASE; each is note-indexed planes in kit-0's
# format. A drum PROGRAM change resolves to a record via a static 3-table LUT keyed by (internal-bank
# -> map row, program) -- NOT program*stride. Verified against the DLL: the GM kits {0,8,16,24,25,32,
# 40,48,56} map to record indices {0,3,9,10,11,17,19,22,30} = Standard/Room/Power/Electronic/TR-808/
# Jazz/Brush/Orchestra/SFX, each with distinct kick tone#s. See memory scvx-drum-kits.
DRUM_KIT_BASE = 0x18AD950
DRUM_KIT_STRIDE = 0x50C
_DRUM_BANK_ROW = 0x19FFBB0    # DAT_181a00bb0 u8: internal-bank code -> map row (0x04->0 mapA, 0x03->1 mapB)
_DRUM_PROG_MAP = 0x19F1EB0    # DAT_1819f2eb0 u8: [row*0x80 + program] -> L2 index (0xFF = undefined)
_DRUM_KIT_IDX  = 0x19F21B0    # DAT_1819f31b0 u16: [L2] -> kit record index

def drum_kit_base(program, row=0):
    """Resolve a drum PROGRAM (+ map row; row 0 = GM/GS map A, row 1 = SC-88 map B) to its kit
       record's file offset. Returns None for an undefined program (engine leaves the kit unchanged).
       row = E.DLL[_DRUM_BANK_ROW + internal_bank]; standard GM/GS drum parts use internal bank 0x04
       -> row 0. Selecting map B needs the SC-88 map-select SysEx (not parsed here)."""
    L2 = DLL[_DRUM_PROG_MAP + row * 0x80 + (program & 0x7f)]
    if L2 == 0xff:
        return None
    idx = struct.unpack_from("<H", DLL, _DRUM_KIT_IDX + L2 * 2)[0]
    return DRUM_KIT_BASE + idx * DRUM_KIT_STRIDE

def drum_key(note, kit=0, base=None):
    """note -> (tone#, level, pitch, group, pan) from a kit record. `base` = the record file offset
       from drum_kit_base(program); default (kit 0) is GM Standard."""
    o = base if base is not None else (DRUM_KIT_BASE + kit * DRUM_KIT_STRIDE)
    tone = struct.unpack_from("<H", DLL, o + note * 2)[0]
    return dict(tone=tone, level=DLL[o + 0x100 + note], pitch=DLL[o + 0x180 + note],
                group=DLL[o + 0x200 + note], pan=DLL[o + 0x280 + note])

def render_drum_note(note, vel, ring=1.8, kit=0, cc7=127, cc11=127, master=127, kit_base=None,
                     attack_ms=None):
    """Render one drum hit. The kit's +0x180 plane gives the KEY to play the tone at (60 = the
       tone's natural pitch); the sample is pitched from its root as usual -- that's why toms,
       whose samples sit at a high root, come out low. A drum rings out: note-off is ignored,
       so `hold` spans the whole render and the tone's own TVA segments do the decay.
       `kit_base` = a resolved kit record offset from drum_kit_base(program); default = GM Standard."""
    tail = 0.4
    attack_ms = DEFAULT_ATTACK_MS if attack_ms is None else attack_ms
    nsamp = int((ring + tail) * SR)
    dk = drum_key(note, kit, base=kit_base)
    tno, key = dk['tone'], 60                         # drums resolve at the neutral key
    # The kit's +0x180 coarse-pitch plane is applied at HALF strength (50 cents per unit), not a
    # whole semitone: measured against the engine, notes sharing a tone (41/43, 45/47) come out
    # 2^(offset/24) apart, e.g. +6 -> 1.189x (engine 1.187x), not 2^(6/12)=1.414x.
    coarse = 2.0 ** ((dk['pitch'] - 60) / 24.0)
    name, zones = D.resolve(tno, key, vel)
    if not zones: return np.zeros((nsamp, 2), np.float32), name, np.zeros(nsamp, np.float32)
    mix = np.zeros(nsamp, np.float32)
    for pi, w, c in zones:
        if c is None or c.get('reverse'): continue
        raw0 = P._block(tno, pi)
        native = c['root'] + (1024 - c['fine']) / 1000.0 + partial_pitch_offset(raw0)
        r = 2.0 ** ((key - native) / 12.0) * coarse
        pe = pitch_env_ratio(raw0, key, vel, ring, tail, nsamp)     # drum pitch-drop, when present
        vb = lfo_pitch_ratio(tno, pi, nsamp)
        for m in (pe, vb):
            if m is not None: r = r * m
        sig = play_wave(c, key, nsamp, ratio=r)
        pp = P.partial_params(tno, pi)
        if pp is not None:
            cb = compute_tvf_env(raw0, vel, ring, tail, key)          # moving cutoff (the drum "punch")
            sig = apply_tvf_varying(sig, cb, pp['tvfType'], tvf_reso_byte(raw0))
            sig = sig * compute_tva_env(raw0, pp, vel, ring, tail, key,
                                        D.zone_level(pp['msamp'], key, pp['keyCenter']),
                                        D.tone_level(tno), attack_ms=attack_ms)
        mix += sig                                   # partials SUM (not averaged)
    gl, gr = pan_gains(dk['pan'])                    # kit +0x280 plane: pan is PER DRUM NOTE
    # Kit level (+0x100 plane) is NOT part of the voice gain -- the per-voice amp matches the engine
    # exactly without it. It enters downstream, in the part-volume computation FUN_180060390:
    #   vol = (partLevel * expression * master) >> 6
    #   vol = (kit_level * vol) >> 7          <- linear, only for a drum part (voice+0x158 != 0)
    #   vol = (vol >> 16) * (vol >> 16)       <- the part gain is SQUARED
    # so the kit level acts as (level/127)^2. Independently corroborated: g_level_curve composed
    # with g_amp_curve is exactly (l/127)^2 (max dev 5.7e-5), i.e. level bytes are amplitude-squared
    # throughout. Using the linear ratio here left a 1.34x spread against the real engine's absolute
    # per-instrument RMS; squaring brings all five measured drums inside 1.047x.
    mono = mix * (dk['level'] / 127.0) ** 2 * part_volume_scale(cc7, cc11, master)
    return np.stack([mono * gl, mono * gr], axis=1), name, mono

def render_drums(events, kit=0, path="our_engine_drums.wav"):
    """events: list of (start_sec, note, vel). Renders a drum pattern to WAV."""
    total = int((max(s for s, n, v in events) + 2.2) * SR)
    mix = np.zeros((total, 2), np.float32)
    for (start, note, vel) in events:
        sig, nm, _mono = render_drum_note(note, vel, kit=kit)
        i = int(start * SR); n = min(len(sig), total - i)
        mix[i:i + n] += sig[:n]
    peak = np.max(np.abs(mix)) or 1.0
    pcm16 = (np.clip(mix / peak * 0.92, -1, 1) * 32767).astype(np.int16).reshape(-1)
    w = wave.open(path, "wb"); w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(pcm16.tobytes()); w.close()
    print(f"wrote {path}  ({len(pcm16)/2/SR:.1f}s stereo, {len(events)} hits)")
    return path

# ---------------------------------------------------------------- sequence -> wav
def render_song(events, program=0, tone_map=4, path="our_engine.wav"):
    """events: list of (start_sec, note, vel, hold_sec).  Renders + mixes to a 16-bit WAV."""
    total = int((max(s + h for s, n, v, h in events) + 2.2) * SR)   # room for the release tail
    mix = np.zeros((total, 2), np.float32)
    for (start, note, vel, hold) in events:
        sig, name, _mono = render_note(program, note, vel, hold, tone_map=tone_map)
        i = int(start * SR)
        n = min(len(sig), total - i)
        mix[i:i + n] += sig[:n]
    peak = np.max(np.abs(mix)) or 1.0
    pcm = np.clip(mix / peak * 0.92, -1, 1)
    pcm16 = (pcm * 32767).astype(np.int16).reshape(-1)
    w = wave.open(path, "wb"); w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(pcm16.tobytes()); w.close()
    print(f"wrote {path}  ({len(pcm16)/2/SR:.1f}s stereo, {len(events)} notes)")
    return path

if __name__ == "__main__":
    # A little demo on Grand Piano (program 0): C-major scale up, then a held C chord.
    scale = [60, 62, 64, 65, 67, 69, 71, 72]
    ev = [(i * 0.34, n, 100, 0.30) for i, n in enumerate(scale)]
    t0 = len(scale) * 0.34 + 0.1
    for n in (60, 64, 67, 72):                            # C major chord, held
        ev.append((t0, n, 96, 1.6))
    render_song(ev, program=0, path=os.path.join(os.path.dirname(__file__), "our_engine_piano.wav"))
