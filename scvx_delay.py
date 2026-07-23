#!/usr/bin/env python
"""scvx_delay.py -- the GS *system* Delay (SysEx macro 40 01 50), the third GS send effect alongside
reverb + chorus. It is NOT the reverb Delay/PanDelay types (those are the reverb tank degenerated).

In SCCore.dll the system delay is woven into the inlined matrix+delay-line block of fx_process_block
(no standalone fx_delay_process), so unlike reverb/chorus it is NOT transcribed line-for-line. Instead
it is a structural model DERIVED FROM THE GS PARAMETERS (read live from g_delay_preset_tbl @181893930)
and the documented Roland conversion tables, then CALIBRATED + VALIDATED against the engine's own
measured wet (wet = engine(delay-send=127) - engine(delay-send=0), the dry cancels). That isolated-wet
measurement is the same in-bounds ground truth used for the reverb/chorus send levels.

Topology (standard GS 3-tap stereo delay + feedback):
  - one mono feedback ring; the feedback tap is the CENTER tap at Tc = timeCenter(ms)*32 samples
  - left tap at Tl = Tc*ratioL%, right tap at Tr = Tc*ratioR%
  - out L = levelL*ring[Tl] + levelC*ring[Tc];  out R = levelR*ring[Tr] + levelC*ring[Tc]
  - ring[wp] = send[i] + feedback*ring[Tc]   (feedback from the center tap; optional pre-LPF)
  - for the Pan* types levelC=0 (silent center) with Tl=0.5Tc, Tr=1.0Tc -> alternating L/R ping-pong
The 10 macro presets set all of the above; a song may also tweak individual params via SysEx 40 01 51..5A.
"""
import numpy as np, os

SR = 32000
RING = 1 << 17           # >= max delay (1000ms*32 = 32000) with headroom
_T = os.path.join(os.path.dirname(__file__), "tables")

# Roland GS conversion tables (from the SC-VA GS map; sibling GsConversionTables 16 & 17).
TIME_MS = [0.1,0.2,0.3,0.4,0.5,0.6,0.7,0.8,0.9,1.0,1.1,1.2,1.3,1.4,1.5,1.6,1.7,1.8,1.9,2.0,2.2,2.4,2.6,2.8,
 3.0,3.2,3.4,3.6,3.8,4.0,4.2,4.4,4.6,4.8,5.0,5.5,6.0,6.5,7.0,7.5,8.0,8.5,9.0,9.5,10.0,11.0,12.0,13.0,
 14.0,15.0,16.0,17.0,18.0,19.0,20.0,22.0,24.0,26.0,28.0,30.0,32.0,34.0,36.0,38.0,40.0,42.0,44.0,46.0,48.0,50.0,
 55.0,60.0,65.0,70.0,75.0,80.0,85.0,90.0,95.0,100.0,110.0,120.0,130.0,140.0,150.0,160.0,170.0,180.0,190.0,200.0,
 220.0,240.0,260.0,280.0,300.0,320.0,340.0,360.0,380.0,400.0,420.0,440.0,460.0,480.0,500.0,550.0,600.0,650.0,700.0,750.0,800.0,850.0,900.0,950.0,1000.0]
RATIO_PCT = [4,8,13,17,21,25,29,33,38,42,46,50,54,58,63,67,71,75,79,83,88,92,96,100,104,108,113,117,121,125,129,133,
 138,142,146,150,154,158,163,167,171,175,179,183,188,192,196,200,204,208,213,217,221,225,229,233,238,242,246,250,
 254,258,263,267,271,275,279,283,288,292,296,300,304,308,313,317,321,325,329,333,338,342,346,350,354,358,363,367,371,375,379,383,388,392,396,400,404,408,413,417,421,425,429,433,438,442,446,450,454,458,463,467,471,475,479,483,488,492,496,500]

def time_ms(raw):   return TIME_MS[max(1, min(115, raw)) - 1]     # DELAY TIME CENTER raw 1..115 -> ms
def ratio_pct(raw): return RATIO_PCT[max(1, min(120, raw)) - 1]   # DELAY TIME RATIO raw 1..120 -> %

# The 10 macro presets, read live from g_delay_preset_tbl @ 0x181893930 (`scdec delaytest`):
# [preLpf, timeCenter, ratioL, ratioR, levelC, levelL, levelR, returnLevel, feedback, sendToReverb]
DELAY_TYPE_NAMES = ["Delay1","Delay2","Delay3","Delay4","PanDelay1","PanDelay2","PanDelay3",
                    "PanDelay4","DelayToReverb","PanRepeat"]
PRESETS = {
 0:[0, 97,1,1,127,0,0,64,80,0], 1:[0,106,1,1,127,0,0,64,80,0], 2:[0,115,1,1,127,0,0,64,72,0],
 3:[0, 83,1,1,127,0,0,64,72,0], 4:[0,105,12,24,0,125,60,64,74,0], 5:[0,109,12,24,0,125,60,64,71,0],
 6:[0,115,12,24,0,120,64,64,73,0], 7:[0,93,12,24,0,120,64,64,72,0], 8:[0,109,12,24,0,114,60,64,61,36],
 9:[0,110,21,32,97,127,67,64,40,0]}

# Fixed INPUT pre-delay of the GS delay: the engine delays the send bus by a constant 60.0ms BEFORE
# the feedback line, on top of the table time. Measured from first-arrival (real repeat lands at
# 60ms + timeCenter, uniformly across every type); a 1920-sample sweep lifts every type's envelope
# correlation to ~0.99. Because the feedback line is LTI, this input pre-delay just shifts the whole
# wet output later by 1920 samples (applied that way -- exact).
PREDELAY = 1920          # 60.0ms * 32 samples/ms

# Overall wet send level at part DELAY SEND=127, calibrated by RMS-matching the engine's OWN isolated
# wet (engine(send=127)-engine(send=0)) on the center-tap types, with the DELAY LEVEL (return) at its
# macro default 64 (the model carries g_ret=ret/127 separately, so this scalar is return-independent).
# Same method + status as the reverb/chorus send scalars. See docs/FINDINGS.md "GS system Delay".
DLY_SEND_127 = 0.356

def send_gain(part_send):
    return DLY_SEND_127 * (int(part_send) / 127.0)

def params_from_raw(raw10):
    """Compile 10 raw GS delay params -> DSP coefficients (tap samples, gains, feedback, pre-LPF)."""
    preLpf, tC, rL, rR, lC, lL, lR, ret, fb, sRev = raw10
    Tc = int(round(time_ms(tC) * SR / 1000.0))
    Tl = int(round(Tc * ratio_pct(rL) / 100.0))
    Tr = int(round(Tc * ratio_pct(rR) / 100.0))
    g_ret = ret / 127.0                     # DELAY LEVEL (return), scales the whole wet
    return dict(Tc=max(1,Tc), Tl=max(1,Tl), Tr=max(1,Tr),
                gC=lC/127.0*g_ret, gL=lL/127.0*g_ret, gR=lR/127.0*g_ret,
                fb=(fb - 64) / 64.0,        # DELAY FEEDBACK raw 0..127 -> -1..~+1 (display -64..+63)
                preLpf=preLpf, sRev=sRev/127.0)

def type_params(t):
    return params_from_raw(PRESETS[t if t is not None else 0])

# DELAY PRE-LPF (0..7): one-pole cutoff index; 0 = bypass (all presets use 0). Coefs approximate the
# GS pre-LPF cutoff ladder; only used if a song sets DELAY PRE-LPF nonzero.
_PRELPF_A = [0.0, 0.10, 0.18, 0.28, 0.40, 0.55, 0.70, 0.84]

def delay(send_mono, params, sr=SR):
    """Run the GS system delay on the mono delay-send bus. Returns (N,2) stereo wet (pre reverb-send).
       Recursive: feedback ring tapped at center; L/R/C output taps per the compiled params."""
    n = len(send_mono)
    p = params
    Tc, Tl, Tr = p['Tc'], p['Tl'], p['Tr']
    gC, gL, gR, fb = p['gC'], p['gL'], p['gR'], p['fb']
    a = _PRELPF_A[p['preLpf']] if 0 <= p['preLpf'] < 8 else 0.0
    D = np.zeros(RING, np.float64)
    outL = np.empty(n); outR = np.empty(n)
    M = RING - 1
    lpf = 0.0
    wp = 0
    for i in range(n):
        c = D[(wp - Tc) & M]                 # center tap (feedback source + center output)
        fbv = c if a == 0.0 else (lpf := lpf * a + (1.0 - a) * c)   # optional pre-LPF in feedback
        D[wp] = send_mono[i] + fb * fbv
        l = D[(wp - Tl) & M]; r = D[(wp - Tr) & M]
        outL[i] = gL * l + gC * c
        outR[i] = gR * r + gC * c
        wp = (wp + 1) & M
    out = np.stack([outL, outR], axis=1)
    if PREDELAY > 0:                      # fixed 60ms input pre-delay == output shift (LTI)
        out = np.concatenate([np.zeros((PREDELAY, 2)), out])[:n]
    return out

def type_dump(t):
    """Uniform interface like scvx_reverb/chorus: return compiled params for GS delay macro t (0..9)."""
    return type_params(t)
