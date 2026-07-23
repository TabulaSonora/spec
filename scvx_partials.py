#!/usr/bin/env python
"""
Static per-partial SYNTHESIS parameters, decoded straight from the 0x6e partial
block in the tone table (tone_a.bin). NO ENGINE.

The engine's partial_load_params/partial_compute_{pitch,filter} + the TVA helpers
read this exact 0x6e block via (*DAT_181a74920)(). Every offset they touch is a
field here. This module names them (confidence tags in comments) so the back half
of the synth (TVA env / TVF filter / pitch env / LFO) can be reconstructed.

Block base within a tone = 0x24 + partial_index*0x6e (see scvx_directory.tone()).
Offsets below are RELATIVE to the block base.
"""
import struct, os
import scvx_directory as D

T = os.path.join(os.path.dirname(__file__), "tables")
tone_b = open(os.path.join(T, "tone_a.bin"), "rb").read()

BLK = 0x6e
HDR = 0x24
STRIDE = 0x100

def _block(tone_no, pi):
    o = tone_no*STRIDE + HDR + pi*BLK
    return tone_b[o:o+BLK]

def u8(b, o):  return b[o]
def s8(b, o):  v=b[o]; return v-0x100 if v>=0x80 else v
def u16(b, o): return b[o] | (b[o+1] << 8)
def s16(b, o): v=u16(b,o); return v-0x10000 if v>=0x8000 else v

NPARTIALS = 2   # melodic tone: stride 0x100 = 0x24 hdr + 2*0x6e. (drum table = 4)

def partial_params(tone_no, pi):
    """Decode the 0x6e block into named synth fields. Confidence tags:
       [c]=confirmed by compute-fn read, [l]=likely, [g]=guess."""
    b = _block(tone_no, pi)
    if len(b) < BLK:
        return None
    ms = u16(b, 2)
    if ms == 0xffff:
        return None
    return {
        # --- identity (already used by the wave directory) ---
        "msamp":      ms,                 # [c] +0x02 multisample index
        "keyCenter":  u8(b, 0x04),        # [c] +0x04 key center (transpose origin)
        "pan":        u8(b, 0x0f),        # [c] +0x0f PER-PARTIAL PAN (0x40 = centre). 894 of ~3100
                                          #     partials are non-neutral; the "w"/wide and "St."
                                          #     stereo patches carry exact mirror pairs about 0x40
                                          #     (St.Soft EP 24/104, Syn.Strings2 29/104). Verified
                                          #     against the engine: centre-panned patches render
                                          #     identical L/R energy, mirrored ones do not.
        # --- pitch (partial_compute_pitch @18005fc20) ---
        "pitchCoarse":  s8(b, 0x11)-0,    # [c] +0x11 (v-0x40)*10 -> coarse, milli-semitone*10
        "pitchModDepth": u8(b, 0x12),     # [c] +0x12 bend/LFO pitch depth
        # --- pitch envelope (gated by FUN_18005fde0; rates keyfollow) ---
        "pEnvDepth":    u16(b, 0x18),     # [c] +0x18 pitch-env depth base
        "pEnvBias":     [s8(b,0x1b)-0x40, s8(b,0x1c)-0x40, s8(b,0x1d)-0x40,
                         s8(b,0x1e)-0x40, s8(b,0x1f)-0x40],  # [c] +0x1b..0x1f stage biases
        "pEnvRateKF0":  u8(b, 0x27),      # [c] +0x27 rate keyfollow curve row
        "pEnvRate0":    u8(b, 0x29),      # [c] +0x29 rate param (FUN_1800607e0)
        "pEnvRate1":    u8(b, 0x2a),      # [c] +0x2a rate param
        "pEnvVelRate":  u8(b, 0x2c),      # [c] +0x2c rate vel-sens (FUN_180060880)
        # --- TVF filter (partial_compute_filter @180061210) ---
        # voice cutoff = block[0x2f]*0x100 + env_peak(from 0x33), clamped 0x7fff.
        "tvfCutoff":    u8(b, 0x2f),      # [c] +0x2f cutoff base (x0x100 -> voice+0x1f0)
        "tvfResonance2": u8(b, 0x30),     # [c] +0x30 RESONANCE (0x40 neutral) -> voice+0xee.
                                          #     Was mislabeled "cutoff key bias". It is offset by the
                                          #     part-level GS TVF Resonance (SysEx 40 1x 33) and its
                                          #     per-program default, floored at 4, then drives BOTH the
                                          #     cutoff ceiling and the filter Q (q = byte/64).
        "tvfType":      u8(b, 0x31),      # [c] +0x31 filter TYPE 0/1/2/4/5/6 (else bypass); idx DAT_181987b00
        "tvfEnvKF":     u8(b, 0x32),      # [c] +0x32 env-depth key-follow (low nibble -> DAT_181a02a20)
        "tvfEnvDepth":  u8(b, 0x33),      # [c] +0x33 env depth (0x40 center; DAT_1819a2fa8/3028)
        "tvfEnvStart":  u16(b, 0x38),     # [l] +0x38 env aux (DAT_181a1f5c4)
        "tvfEnvLevels": [u8(b,0x3a), u8(b,0x3b), u8(b,0x3c),
                         u8(b,0x3d), u8(b,0x3e)],  # [c] +0x3a..0x3e 5 stage levels (FUN_180061640)
        "tvfEnvRateKF": u8(b, 0x46),      # [c] +0x46 env-rate key-follow (DAT_181a03920)
        "tvfResonance": u8(b, 0x4a),      # [c] +0x4a resonance (0x40 center; DAT_1819a2b88)
        # --- TVA amplitude (FUN_180060960 base, FUN_180060b00 env levels) ---
        "tvaLevel":     u8(b, 0x53),      # [c] +0x53 TVA base level -> DAT_1819a2a00
        "tvaLevelKF":   u8(b, 0x54),      # [c] +0x54 level key-follow row -> DAT_181a026a0
        "tvaLevelKFDep":u8(b, 0x55),      # [c] +0x55 level key-follow depth
        "tvaEnvRateL":  [u8(b,0x5a), u8(b,0x5b), u8(b,0x5c), u8(b,0x5d)], # [c] +0x5a..0x5d env stage rate/level offsets
        "tvaRateKF0":   u8(b, 0x65),      # [c] +0x65 attack-rate keyfollow row
        "tvaRateKF1":   u8(b, 0x66),      # [c] +0x66 decay-rate keyfollow row
        "tvaRate0":     u8(b, 0x67),      # [c] +0x67 rate param (FUN_1800607e0)
        "tvaRate1":     u8(b, 0x68),      # [c] +0x68 rate param
        "tvaVelRate0":  u8(b, 0x69),      # [c] +0x69 rate vel-sens (FUN_180060880)
        "tvaVelRate1":  u8(b, 0x6a),      # [c] +0x6a rate vel-sens
        "tvaLevelKF2":  u8(b, 0x6b),      # [c] +0x6b level keyfollow (DAT_181a01fa0)
        # velocity gate (already used by directory) lives at +0x4f/0x51
        "velLo":        u8(b, 0x4f),      # [c]
        "velHi":        u8(b, 0x51),      # [c]
        "level":        u8(b, 0x50),      # [c]
    }

_XF = open(os.path.join(T, "curve_velxfade_01020.bin"), "rb").read()   # DAT_181a01020, 16 rows x 0x80

def partial_level(raw, vel):
    """partial_velocity_gate @180003e90 -> the byte the engine stores at voice+0x164.

       This is NOT the MIDI velocity (that lives at voice+0x166). It is the partial's own LEVEL,
       crossfaded across its velocity window: block[0x50] is the level at one end of the window and
       block[0x52] the level at the other, with block[0x4e]'s low nibble selecting the crossfade
       shape from DAT_181a01020 (row 0 linear, higher rows progressively concave). Returns None if
       the partial is gated off by its velocity window (block[0x4f]/block[0x51]).

       Everything downstream that we used to feed raw velocity takes THIS instead:
       tva_compute_base_level's second attenuation, and the two env_level_scale rate multipliers
       (@18005f0aa reads param_1+0x164 for both). Verified exact against the engine's live voice
       struct on 7 instruments / 11 partials."""
    lo, hi = raw[0x4f], raw[0x51]
    b11, b10 = min(lo, hi), max(lo, hi)
    if vel < b11 or vel > b10: return None
    if b10 == b11:
        b5 = 0x7f
    else:
        u6 = vel - b11
        u7 = ((u6 * 0x100 | u6 >> 8) & 0xffff) // (b10 - b11)
        b5 = 0x7f if u7 > 0xff else (u7 >> 1)
    span = (raw[0x52] - raw[0x50]) & 0xff
    s = span - 0x100 if span >= 0x80 else span                  # signed char
    idx = b5 if lo <= hi else ((b11 - b5) & 0xff)               # window runs backwards if lo > hi
    c4 = (((_XF[(raw[0x4e] & 0xf) * 0x80 + idx] * abs(s) + 0x7f) * 2) >> 8) & 0xff
    if c4 >= 0x80: c4 -= 0x100
    v = (raw[0x50] + (c4 if s >= 0 else -c4)) & 0xff
    if v >= 0x80: v -= 0x100
    return 1 if v == 0 else v

def dump(tone_no):
    tn = D.tone(tone_no)
    if not tn:
        print(f"tone#{tone_no}: (empty)"); return
    print(f"tone#{tone_no} '{tn['name']}'")
    for pi in range(NPARTIALS):
        p = partial_params(tone_no, pi)
        if not p:
            continue
        print(f"  partial {pi}: ms#{p['msamp']} kc={p['keyCenter']} "
              f"vel[{p['velLo']}-{p['velHi']}] lvl={p['level']}")
        print(f"    pitch: coarse(raw{_block(tone_no,pi)[0x11]:#04x}) depth={p['pitchModDepth']} "
              f"pEnvDepth={p['pEnvDepth']} bias={p['pEnvBias']}")
        print(f"    tvf:   type={p['tvfType']} cutoff={p['tvfCutoff']} res={p['tvfResonance']} "
              f"envDepth(0x40c)={p['tvfEnvDepth']} envLevels={p['tvfEnvLevels']}")
        print(f"    tva:   level={p['tvaLevel']} lvlKF={p['tvaLevelKF']} "
              f"envL={p['tvaEnvRateL']} rate0={p['tvaRate0']} rate1={p['tvaRate1']}")

if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1:
        for a in sys.argv[1:]:
            dump(int(a))
    else:
        for t in [0, 39, 71, 73]:   # Piano1, Harpsichord, Marimba, Flute-ish
            dump(t); print()
