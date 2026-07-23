#!/usr/bin/env python
"""
scvx_reverb.py -- the GS reverb, transcribed EXACTLY from SCCore.dll's fx_reverb_process
(@180086140). A Schroeder/Dattorro tank: input conditioner (DC-block + HF-damp) -> 4 series
allpass diffusers -> 2 parallel tanks (each: damping lowpass + 2 nested allpasses + 8 output taps)
-> stereo output mix. Runs on a shared 64K-float ring; the write cursor decrements per sample.

Topology is from the decompiled code; the coefficients and tap positions are READ FROM THE LIVE DLL
(`scdec revdump`), i.e. measured from the engine's own runtime-computed reverb state -- not fitted to
audio. Feed the mono reverb-send bus in; get stereo wet L/R out.
"""
import numpy as np, re, os

RING = 65536
_T = os.path.join(os.path.dirname(__file__), "tables")

# CC91 reverb-send -> send-bus gain. Measured (all by matching the engine's isolated wet return):
#   - LINEAR in CC91 (ratio matches cc/127 exactly, not squared like the volume law),
#   - POST-fader: the wet scales with CC7 AND CC11 (so the send is fed post-pv),
#   - PAN-INDEPENDENT: fed from each note's PRE-PAN mono (render_note's `mono`), like the engine's
#     mono send bus -- NOT the post-pan sig.mean(1), which wrongly attenuated panned notes.
# REV_SEND_127 = send scale at CC91=127, RMS-matched on a SUSTAINED note (strings); this makes a full
# mix (town.mid) land at wet ratio ~1.04. (A short marimba impulse mis-calibrated to 0.68 -- an
# artifact of RMS-windowing a decaying tail, not a real level.) The network is exact from
# fx_reverb_process; this scalar only sets the send level.
REV_SEND_127 = 1.02

def send_gain(cc91):
    return REV_SEND_127 * (int(cc91) / 127.0)

_default_dump = None
def default_dump():
    """The GM-default (Hall) reverb coefficients, dumped from the live DLL (tables/reverb_gm_default.txt)."""
    global _default_dump
    if _default_dump is None:
        _default_dump = load_dump(os.path.join(_T, "reverb_gm_default.txt"))
    return _default_dump

# GS reverb macro (SysEx 40 01 30) -> algorithm, per RolandSysEx table 13. Each was dumped live
# (`scdec revdump <out> <type>`) into tables/reverb_type_<n>_<name>.txt. The network TOPOLOGY is
# identical across all 8 (same struct, same fx_reverb_process code path); only the coefficients/taps
# differ -- Delay/PanDelay just zero the allpass diffusers (coef 1e-05) and collapse to one long tap
# (PanDelay reading different L/R taps for the stereo bounce). So reverb() runs every type unchanged.
REVERB_TYPE_NAMES = ["Room1","Room2","Room3","Hall1","Hall2","Plate","Delay","PanDelay"]
_type_dumps = {}
def type_dump(t):
    """Reverb coefficients for GS reverb macro `t` (0..7). None -> GS default (Hall2, macro 4)."""
    if t is None:
        return default_dump()
    global _type_dumps
    if t not in _type_dumps:
        path = os.path.join(_T, f"reverb_type_{t}_{REVERB_TYPE_NAMES[t]}.txt")
        _type_dumps[t] = load_dump(path) if os.path.exists(path) else default_dump()
    return _type_dumps[t]

def load_dump(path):
    """Parse a `scdec revdump` file -> the reverb coefficient/tap set."""
    txt = open(path).read()
    d = {}
    def small(line):
        m = re.search(r'writeTap=([0-9A-Fa-f]+) readTap=([0-9A-Fa-f]+) coefA=(\S+) coefB=(\S+)', line)
        return dict(wt=int(m.group(1),16), rt=int(m.group(2),16), cA=float(m.group(3)), cB=float(m.group(4)))
    for ln in txt.splitlines():
        if ln.startswith('ap'):
            d[ln.split()[0]] = small(ln)
        elif ln.startswith(('LA ','LB ')):
            nm = ln.split()[0]
            taps = {k: int(v,16) for k,v in re.findall(r'(tap[0-9A-Fa-f]+)=([0-9A-Fa-f]+)', ln)}
            m = re.search(r'cA=(\S+) cB=(\S+)', ln); taps['cA']=float(m.group(1)); taps['cB']=float(m.group(2))
            d[nm] = taps
        elif '.sA' in ln:
            d[ln.split()[0]] = small(ln)
        elif ln.startswith('injTap'):  d['injTap'] = int(ln.split('=')[1],16)
        elif ln.startswith('damp'):
            d['aa8'] = float(re.search(r'aa8_fb=(\S+)', ln).group(1))
            d['aac'] = float(re.search(r'aac_in=(\S+)', ln).group(1))
        elif ln.startswith('gain'):
            for k,g in re.findall(r'(ed70_in|ee70_inj|eef0_fb|edf0_out)=(\S+)', ln): d[k]=float(g)
    return d

def reverb(send_mono, dump, chorus_l=None, chorus_r=None):
    """Run the reverb network on the mono reverb-send bus. Returns (N,2) stereo wet output.
       chorus_l/chorus_r: optional chorus->reverb feedback sends (else 0)."""
    n = len(send_mono)
    D = np.zeros(RING, np.float64)
    d = dump
    ap = [d['ap0'], d['ap1'], d['ap2'], d['ap3']]
    LA, LB = d['LA'], d['LB']
    LAa0, LAa1 = d['LA.sA0'], d['LA.sA1']
    LBa0, LBa1 = d['LB.sA0'], d['LB.sA1']
    inj_tap = d['injTap']
    aa8, aac = d['aa8'], d['aac']
    g_in, g_inj, g_fb, g_out = d['ed70_in'], d['ee70_inj'], d['eef0_fb'], d['edf0_out']
    cl = chorus_l if chorus_l is not None else np.zeros(n)
    cr = chorus_r if chorus_r is not None else np.zeros(n)
    outL = np.empty(n); outR = np.empty(n)
    aa0 = aa4 = 0.0
    stA = stB = 0.0
    wp = 0
    M = 0xffff
    for i in range(n):
        # --- input conditioner ---
        x = (cl[i] + send_mono[i] + cr[i]) * 0.99804 + aa0
        aa0 = aa0 + x * -0.003919
        aa4 = aa4 * aa8 + aac * x
        D[(0x1000 + wp) & M] = aa4 * g_in
        # --- 4 series allpass diffusers ---
        inj = D[(inj_tap + wp) & M] * g_inj
        prev = inj
        dd = 0.0
        for k, ncfg in enumerate(ap):
            dk = D[(ncfg['rt'] + wp) & M]
            if k == 0:
                wk = dk * ncfg['cA'] + inj
            else:
                wk = dk * ncfg['cA'] + prevw * ap[k-1]['cB'] + prevd
            D[(ncfg['wt'] + wp) & M] = wk
            prevw = wk; prevd = dk
        carry = prevw * ap[3]['cB'] + prevd
        fb = g_fb
        # --- Tank A ---
        lpA = D[(LA['tap1C'] + wp) & M] * LA['cB'] + LA['cA'] * stA; stA = lpA
        a0 = D[(LAa0['rt'] + wp) & M]
        p = lpA * fb + carry + a0 * LAa0['cA']
        D[(LAa0['wt'] + wp) & M] = p
        D[(LA['tap10'] + wp) & M] = p * LAa0['cB'] + a0
        a1 = D[(LAa1['rt'] + wp) & M]
        q = a1 * LAa1['cA'] + D[(LA['tap14'] + wp) & M]
        D[(LAa1['wt'] + wp) & M] = q
        D[(LA['tap18'] + wp) & M] = q * LAa1['cB'] + a1
        # --- Tank B (same carry, same fb) ---
        lpB = D[(LB['tap1C'] + wp) & M] * LB['cB'] + LB['cA'] * stB; stB = lpB
        b0 = D[(LBa0['rt'] + wp) & M]
        p2 = lpB * fb + carry + b0 * LBa0['cA']
        D[(LBa0['wt'] + wp) & M] = p2
        D[(LB['tap10'] + wp) & M] = p2 * LBa0['cB'] + b0
        b1 = D[(LBa1['rt'] + wp) & M]
        q2 = b1 * LBa1['cA'] + D[(LB['tap14'] + wp) & M]
        D[(LBa1['wt'] + wp) & M] = q2
        D[(LB['tap18'] + wp) & M] = q2 * LBa1['cB'] + b1
        # --- output taps (latched at pre-decrement wp) ---
        L = (D[(LA['tap28']+wp)&M] + D[(LA['tap20']+wp)&M] + D[(LB['tap28']+wp)&M] + D[(LB['tap20']+wp)&M]) * g_out
        R = (D[(LA['tap2C']+wp)&M] + D[(LA['tap24']+wp)&M] + D[(LB['tap2C']+wp)&M] + D[(LB['tap24']+wp)&M]) * g_out
        outL[i] = L; outR[i] = R
        wp = (wp - 1) & M
    return np.stack([outL, outR], axis=1)
