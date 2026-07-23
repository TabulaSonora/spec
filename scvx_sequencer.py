#!/usr/bin/env python
"""
scvx_sequencer.py -- the time-varying controller / playback layer on top of scvx_engine.

The per-note laws are all latched at note-on (wave, TVA/TVF/pitch envelopes, LFO). The part-level
laws that the engine RE-READS every render block -- CC7 volume, CC11 expression, master volume,
pitch bend, RPN tune, CC64 damper -- are what this layer tracks OVER TIME and folds back into each
sounding note as a per-sample curve (render_note's pv_curve / pitch_add_curve seams).

Timing model (RE'd, TG_Process): host events are timestamped and applied at 32-SAMPLE render-block
boundaries (~1 ms) -- finer than the 100 Hz control tick. We quantize everything to that 32-sample
grid, which is exactly what tools/decoder `seq` mode does, so the two renders line up block-for-block.

Damper (CC64): part+0x462, held while >= 0x40. A note-off arriving with the pedal down defers the
voice's release until the pedal lifts. (voice_block_process @180080e40, FUN_180080d10.)

Input is the SAME script format as `scdec.exe seq`:
    <samplePos> <status> [d1] [d2]     short message (decimal; status includes channel nibble)
    <samplePos> sx <hex> <hex> ...      sysex bytes (hex, incl F0..F7)
so a sequence built here can be dumped to a script, rendered by the DLL for ground truth, and
rendered here, from one identical event list.
"""
import numpy as np, wave, struct
import scvx_engine as E
import scvx_directory as D
import scvx_reverb as RV
import scvx_chorus as CH
import scvx_delay as DL

SR  = E.SR       # 32000
BLK = 32         # render-block = MIDI application granularity

# ----------------------------------------------------------------- script I/O
def parse_script(path):
    """-> list of events. Short: (pos, 'm', status, d1, d2). SysEx: (pos, 'sx', bytes)."""
    evs = []
    for raw in open(path):
        s = raw.strip()
        if not s or s[0] == '#': continue
        t = s.split()
        pos = int(t[0])
        if t[1] in ('sx', 'SX'):
            evs.append((pos, 'sx', bytes(int(x, 16) for x in t[2:])))
        else:
            st = int(t[1]); d1 = int(t[2]) if len(t) > 2 else 0; d2 = int(t[3]) if len(t) > 3 else 0
            evs.append((pos, 'm', st, d1, d2))
    evs.sort(key=lambda e: e[0])
    return evs

def write_script(events, path):
    """events: (pos,'m',status,d1,d2) | (pos,'sx',bytes). Writes the shared seq-script format."""
    with open(path, 'w') as f:
        for e in sorted(events, key=lambda e: e[0]):
            if e[1] == 'sx':
                f.write(f"{e[0]} sx " + " ".join(f"{b:02x}" for b in e[2]) + "\n")
            else:
                f.write(f"{e[0]} {e[2]} {e[3]} {e[4]}\n")
    return path

# small helpers to author sequences (all times in SECONDS -> samples on the 32-grid)
def _q(pos):  return (int(round(pos)) // BLK) * BLK
def ev_prog(t, ch, prog):        return (_q(t*SR), 'm', 0xC0|ch, prog, 0)
def ev_bank(t, ch, msb, lsb=0):  return [(_q(t*SR),'m',0xB0|ch,0,msb),(_q(t*SR),'m',0xB0|ch,32,lsb)]
def ev_cc(t, ch, cc, v):         return (_q(t*SR), 'm', 0xB0|ch, cc, v)
def ev_on(t, ch, note, vel):     return (_q(t*SR), 'm', 0x90|ch, note, vel)
def ev_off(t, ch, note):         return (_q(t*SR), 'm', 0x80|ch, note, 0)
def ev_bend(t, ch, val14):       return (_q(t*SR), 'm', 0xE0|ch, val14 & 0x7f, (val14>>7)&0x7f)

# ----------------------------------------------------------------- timelines
def _zoh(breaks, start, n, default):
    """Zero-order-hold a step function (list of (pos,value)) over [start, start+n) at sample res.
       Controllers change only on 32-grid event positions, so sample-res searchsorted == block ZOH."""
    if not breaks:
        return np.full(n, default, float)
    bp  = np.array([b[0] for b in breaks]); bv = np.array([b[1] for b in breaks], float)
    idx = np.arange(start, start+n)
    j   = np.searchsorted(bp, idx, side='right') - 1
    out = np.where(j < 0, default, bv[np.clip(j, 0, len(bv)-1)])
    return out

def _val_at(breaks, pos, default):
    """Latched value of a step function at an absolute sample position (for note-on latching)."""
    v = default
    for p, x in breaks:
        if p <= pos: v = x
        else: break
    return v

# ----------------------------------------------------------------- parse -> parts + notes
GM_DEFAULT = dict(cc7=100, cc11=127, pan=64, master=127)   # GM/GS post-reset controller defaults

def build_parts(events):
    """Turn the flat event list into: per-part controller timelines, latched program/bank timelines,
       and a list of note records (with damper-extended release). Returns (parts, notes)."""
    NCH = 16
    P = {c: dict(cc7=[], cc11=[], pan=[], bend=[], brange=[(0,2)], tune=[(0,0.0)],
                 damper=[], mod=[], rev=[], cho=[], dly=[], prog=[(0,0)], bank=[(0,0)],
                 rpn_msb=0x7f, rpn_lsb=0x7f, dr_msb=0, dr_lsb=0, open={}) for c in range(NCH)}
    master = []
    notes  = []
    rev_type = []   # (pos, type) GS reverb macro  (SysEx 40 01 30) -- global, selects reverb algorithm
    cho_type = []   # (pos, type) GS chorus macro  (SysEx 40 01 38) -- global, selects chorus preset
    dly_type = []   # (pos, type) GS delay  macro  (SysEx 40 01 50) -- global, selects delay preset

    def close_note(ch, note, off_pos):
        st = P[ch]['open'].pop(note, None)
        if st is None: return
        on_pos, vel = st
        notes.append(dict(ch=ch, note=note, vel=vel, on=on_pos, off=off_pos,
                          prog=_val_at(P[ch]['prog'], on_pos, 0),
                          bank=_val_at(P[ch]['bank'], on_pos, 0),
                          pan =_val_at(P[ch]['pan'],  on_pos, GM_DEFAULT['pan']),
                          cc7 =_val_at(P[ch]['cc7'],  on_pos, GM_DEFAULT['cc7']),
                          cc11=_val_at(P[ch]['cc11'], on_pos, GM_DEFAULT['cc11']),
                          cc91=_val_at(P[ch]['rev'],  on_pos, 40),    # reverb send (GM default 40)
                          cc93=_val_at(P[ch]['cho'],  on_pos, 0),      # chorus send (GM default 0)
                          cdly=_val_at(P[ch]['dly'],  on_pos, 0)))     # delay send (SysEx-only; default 0)

    for e in events:
        pos = e[0]
        if e[1] == 'sx':
            b = e[2]
            # GM/GS master volume: F0 7F 7F 04 01 ll mm F7  -> 7-bit mm
            if len(b) >= 8 and b[0]==0xF0 and b[1]==0x7F and b[3]==0x04 and b[4]==0x01:
                master.append((pos, b[6]))
            # Roland GS DT1: F0 41 dd 42 12 <a1 a2 a3> <data...> cksum F7
            elif len(b) >= 11 and b[0]==0xF0 and b[1]==0x41 and b[3]==0x42 and b[4]==0x12:
                a1, a2, a3, dv = b[5], b[6], b[7], b[8]
                if a1==0x40 and a2==0x01:
                    if   a3==0x30 and dv <= 7: rev_type.append((pos, dv))   # REVERB MACRO
                    elif a3==0x31 and dv <= 7: rev_type.append((pos, dv))   # REVERB CHARACTER (algorithm only)
                    elif a3==0x38 and dv <= 7: cho_type.append((pos, dv))   # CHORUS MACRO
                    elif a3==0x50 and dv <= 9: dly_type.append((pos, dv))   # DELAY MACRO
                elif a1==0x40 and (a2 & 0xF0)==0x10 and a3==0x2C:           # part DELAY SEND LEVEL (no CC)
                    blk = a2 & 0x0F
                    dch = 9 if blk==0 else (blk-1 if blk<=9 else blk)       # GS block -> MIDI channel
                    P[dch]['dly'].append((pos, dv))
            continue
        st, d1, d2 = e[2], e[3], e[4]
        ch = st & 0x0f; hi = st & 0xf0
        if hi == 0x90 and d2 > 0:
            # a re-strike of a still-open note closes the old voice first
            if d1 in P[ch]['open']: close_note(ch, d1, pos)
            P[ch]['open'][d1] = (pos, d2)
        elif hi == 0x80 or (hi == 0x90 and d2 == 0):
            # note-off: if the damper is down, defer release to the next pedal-up (handled below)
            if _val_at(P[ch]['damper'], pos, 0) >= 0x40:
                P[ch].setdefault('sustained', []).append((d1, pos))   # (note, requested-off)
            else:
                close_note(ch, d1, pos)
        elif hi == 0xC0:
            P[ch]['prog'].append((pos, d1))
        elif hi == 0xE0:
            P[ch]['bend'].append((pos, d1 | (d2 << 7)))
        elif hi == 0xB0:
            cc, v = d1, d2
            if   cc == 7:  P[ch]['cc7'].append((pos, v))
            elif cc == 1:  P[ch]['mod'].append((pos, v))    # mod wheel -> LFO1 pitch vibrato depth
            elif cc == 91: P[ch]['rev'].append((pos, v))    # reverb send level
            elif cc == 93: P[ch]['cho'].append((pos, v))    # chorus send level
            elif cc == 11: P[ch]['cc11'].append((pos, v))
            elif cc == 10: P[ch]['pan'].append((pos, v))
            elif cc == 0:  P[ch]['bank'].append((pos, (_val_at(P[ch]['bank'],pos,0) & 0x7f) | (v<<7) if False else v))
            elif cc == 32: pass  # bank LSB: variations use MSB (CC0) in this engine; keep MSB as bank
            elif cc == 64:
                P[ch]['damper'].append((pos, v))
                if v < 0x40:  # pedal up -> release everything sustained at this instant
                    for (note, offp) in P[ch].pop('sustained', []):
                        close_note(ch, note, pos)
            elif cc == 101: P[ch]['rpn_msb'] = v
            elif cc == 100: P[ch]['rpn_lsb'] = v
            elif cc == 6:   _apply_rpn(P[ch], pos, msb=v)
            elif cc == 38:  _apply_rpn(P[ch], pos, lsb=v)
            elif cc in (120, 123):  # all-sound-off / all-notes-off
                for note in list(P[ch]['open']): close_note(ch, note, pos)
                P[ch].pop('sustained', None)
            elif cc == 121:         # reset all controllers
                P[ch]['cc11'].append((pos, 127)); P[ch]['bend'].append((pos, 8192))
                P[ch]['damper'].append((pos, 0)); P[ch]['mod'].append((pos, 0))
    # close anything still ringing at the tail
    end = max((e[0] for e in events), default=0)
    for ch in range(NCH):
        for (note, offp) in P[ch].pop('sustained', []): close_note(ch, note, offp)
        for note in list(P[ch]['open']): close_note(ch, note, end)
    fx = dict(rev_type=rev_type, cho_type=cho_type, dly_type=dly_type)
    return P, master, notes, fx

def _apply_rpn(part, pos, msb=None, lsb=None):
    """Data-entry commit for the selected RPN. GM: 0000=bend range(semis), 0001=fine tune,
       0002=coarse tune. tune stored in milli-semitones (1 cent = 10)."""
    sel = (part['rpn_msb'], part['rpn_lsb'])
    if msb is not None: part['dr_msb'] = msb
    if lsb is not None: part['dr_lsb'] = lsb
    if sel == (0, 0):
        if msb is not None: part['brange'].append((pos, msb))       # +/- msb semitones
    elif sel == (0, 1):
        val = (part['dr_msb'] << 7) | part['dr_lsb']                 # 14-bit, 8192 = 0 cents
        part['tune'].append((pos, (val - 8192) / 8192.0 * 1000.0))  # +/-100 cents = +/-1000 ms
    elif sel == (0, 2):
        if msb is not None: part['tune'].append((pos, (msb - 0x40) * 1000.0))  # coarse: +/-semitone

# ----------------------------------------------------------------- render
def render_events(events, tail=2.2, out="our_seq.wav", tone_map=4, verbose=True,
                  t_end=None, drum_channel=9, reverb=False, chorus=False, delay=False,
                  rev_type=None, cho_type=None, dly_type=None, attack_ms=None):
    """Render an event list to a stereo WAV, absolute-level matched to the DLL (fixed x32767 gain).
       `t_end` (seconds) truncates the render; `drum_channel` (default 9) routes to the drum path.
       `reverb=True` / `chorus=True` / `delay=True` run the transcribed GS reverb/chorus/system-delay
       on per-note send buses and mix the wet returns in. Reverb/chorus send is CC91/CC93; the GS delay
       send has NO CC -- it is the SysEx part DELAY SEND LEVEL (40 1x 2C).
       `rev_type`/`cho_type`/`dly_type` force a GS algorithm; None auto-detects the macro SysEx in the
       stream (falling back to the GS defaults: Hall2 / Chorus3 / Delay1)."""
    P, master, notes, fx = build_parts(events)
    # resolve the active FX algorithm: explicit override > macro SysEx in the file > GS default (None)
    if rev_type is None and fx['rev_type']: rev_type = fx['rev_type'][0][1]
    if cho_type is None and fx['cho_type']: cho_type = fx['cho_type'][0][1]
    if dly_type is None and fx['dly_type']: dly_type = fx['dly_type'][0][1]
    end = max((e[0] for e in events), default=0)
    if t_end is not None: end = min(end, int(t_end * SR))
    total = end + int(tail * SR)
    mix = np.zeros((total, 2), np.float32)
    send  = np.zeros(total, np.float32) if reverb else None   # mono reverb-send bus (all parts)
    csend = np.zeros(total, np.float32) if chorus else None   # mono chorus-send bus (all parts)
    dsend = np.zeros(total, np.float32) if delay  else None   # mono delay-send bus  (all parts)

    # --- drum assign/exclusive groups (choke): a drum note in group G is cut when the NEXT note in
    #     the same group hits -- open hi-hat silenced by closed/pedal, mute/open cuica pairs, etc.
    #     The engine's cut is near-instant (~10 ms: full at the strike, ~0.86 at +5 ms, 0 by +10 ms).
    #     Group comes from the kit's +0x200 plane (E.drum_key). choke = samples-until-cut per note.
    # A drum PROGRAM change on the drum channel selects the kit (drum_kit_base); an undefined program
    # leaves the current kit in place. Resolve per note (groups are kit-specific too), then choke.
    drum_choke = {}; _last_grp = {}; drum_base = {}; _cur_base = {}
    for nd in sorted((n for n in notes if n['ch'] == drum_channel and n['on'] < total),
                     key=lambda n: n['on']):
        b = E.drum_kit_base(nd['prog'], row=0)           # None -> undefined -> keep current kit
        if b is not None: _cur_base[nd['ch']] = b
        base = _cur_base.get(nd['ch'], E.DRUM_KIT_BASE)
        drum_base[id(nd)] = base
        g = E.drum_key(nd['note'], base=base)['group']
        if not g: continue
        prev = _last_grp.get(g)
        if prev is not None and nd['on'] > prev['on']:
            drum_choke[id(prev)] = nd['on'] - prev['on']
        _last_grp[g] = nd

    for nd in notes:
        ch, on = nd['ch'], nd['on']
        if on >= total: continue
        # --- drum channel: each hit rings out, timbre + level latched at note-on ---
        if ch == drum_channel:
            sig, name, mono = E.render_drum_note(nd['note'], nd['vel'], kit=0,
                                           kit_base=drum_base.get(id(nd)),
                                           cc7=nd['cc7'], cc11=nd['cc11'],
                                           master=_val_at(master, on, GM_DEFAULT['master']),
                                           attack_ms=attack_ms)
            choke = drum_choke.get(id(nd))                # cut when the next same-group note hits
            if choke is not None and choke < len(sig):
                sig = sig.copy(); mono = mono.copy(); s = choke + 128    # ~4ms hold, ~6ms fade to 0
                if s < len(sig):
                    f = min(192, len(sig) - s)
                    sig[s:s+f] *= np.linspace(1.0, 0.0, f)[:, None]
                    sig[s+f:] = 0.0
                    mono[s:s+f] *= np.linspace(1.0, 0.0, f); mono[s+f:] = 0.0
            n = min(len(sig), total - on); mix[on:on+n] += sig[:n]
            if send is not None and nd['cc91'] > 0:
                send[on:on+n] += mono[:n] * RV.send_gain(nd['cc91'])
            if csend is not None and nd['cc93'] > 0:
                csend[on:on+n] += mono[:n] * CH.send_gain(nd['cc93'])
            if dsend is not None and nd['cdly'] > 0:
                dsend[on:on+n] += mono[:n] * DL.send_gain(nd['cdly'])
            continue
        hold = (nd['off'] - on) / SR
        nsamp = int((hold + tail) * SR)
        nsamp = min(nsamp, total - on)
        pr = P[ch]
        # --- volume curve: CC7 x CC11 x master over the note's life, as the engine re-reads it ---
        cc7 = _zoh(pr['cc7'],  on, nsamp, GM_DEFAULT['cc7'])
        c11 = _zoh(pr['cc11'], on, nsamp, GM_DEFAULT['cc11'])
        mst = _zoh(master,     on, nsamp, GM_DEFAULT['master'])
        pvc = _pv_vec(cc7, c11, mst)
        # --- pitch curve: bend (14-bit x range) + RPN tune, milli-semitones ---
        bnd = _zoh(pr['bend'],   on, nsamp, 8192)
        brg = _zoh(pr['brange'], on, nsamp, 2)
        tun = _zoh(pr['tune'],   on, nsamp, 0.0)
        padd = (bnd - 8192) / 8192.0 * brg * 1000.0 + tun
        static = (not np.any(np.diff(padd))) and padd[0] == 0.0
        # --- mod wheel (CC1) -> LFO1 pitch vibrato depth, per control tick over the note ---
        mod_series = None
        if pr['mod'] and any(v > 0 for _, v in pr['mod']):
            ntk = int(np.ceil(nsamp / 320.0)) + 1
            cc1_tick = [_val_at(pr['mod'], on + k * 320, 0) for k in range(ntk)]
            mod_series = np.array([E.mod_wheel_depth_ms(int(v)) for v in cc1_tick], float)
        # --- pan curve: only when CC10 moves while the note sounds (else the latched scalar) ---
        pan_curve = None
        if any(on < p < on + nsamp for p, _ in pr['pan']):
            pan_curve = _zoh(pr['pan'], on, nsamp, GM_DEFAULT['pan'])
        sig, name, mono = E.render_note(nd['prog'], nd['note'], nd['vel'], hold, tail=tail,
                                  tone_map=tone_map, part_pan=nd['pan'], bank=nd['bank'],
                                  pv_curve=pvc,
                                  pitch_add_curve=None if static else padd,
                                  mod_series=mod_series, pan_curve=pan_curve, attack_ms=attack_ms)
        n = min(len(sig), total - on)
        mix[on:on+n] += sig[:n]
        if send is not None and nd['cc91'] > 0:
            send[on:on+n] += mono[:n] * RV.send_gain(nd['cc91'])
        if csend is not None and nd['cc93'] > 0:
            csend[on:on+n] += mono[:n] * CH.send_gain(nd['cc93'])
        if dsend is not None and nd['cdly'] > 0:
            dsend[on:on+n] += mono[:n] * DL.send_gain(nd['cdly'])
        if verbose: print(f"  note ch{ch} {name} n{nd['note']} v{nd['vel']} "
                          f"hold={hold:.2f}s cc11[{int(c11[0])}..{int(c11[-1])}]")

    if csend is not None and np.any(csend):       # transcribed GS chorus on the chorus-send bus
        cwet = CH.chorus(csend.astype(np.float64), CH.type_dump(cho_type))
        mix = mix + cwet.astype(np.float32)
        if verbose:
            cn = CH.CHORUS_TYPE_NAMES[cho_type] if cho_type is not None else "Chorus3(default)"
            print(f"  chorus[{cn}]: wet rms={np.sqrt((cwet**2).mean()):.5f}")
    if dsend is not None and np.any(dsend):       # GS system delay on the delay-send bus (SysEx send)
        dwet = DL.delay(dsend.astype(np.float64), DL.type_dump(dly_type))
        mix = mix + dwet.astype(np.float32)
        if verbose:
            dn = DL.DELAY_TYPE_NAMES[dly_type] if dly_type is not None else "Delay1(default)"
            print(f"  delay[{dn}]: wet rms={np.sqrt((dwet**2).mean()):.5f}")
    if send is not None and np.any(send):          # transcribed GS reverb on the reverb-send bus
        wet = RV.reverb(send.astype(np.float64), RV.type_dump(rev_type))
        mix = mix + wet.astype(np.float32)
        if verbose:
            rn = RV.REVERB_TYPE_NAMES[rev_type] if rev_type is not None else "Hall2(default)"
            print(f"  reverb[{rn}]: wet rms={np.sqrt((wet**2).mean()):.5f}")

    pcm = np.clip(mix * 32767.0, -32768, 32767).astype(np.int16).reshape(-1)
    w = wave.open(out, "wb"); w.setnchannels(2); w.setsampwidth(2); w.setframerate(SR)
    w.writeframes(pcm.tobytes()); w.close()
    if verbose: print(f"wrote {out} ({total/SR:.1f}s stereo, {len(notes)} notes, peak={np.max(np.abs(mix)):.4f})")
    return out

def _pv_vec(cc7, cc11, master):
    """Vectorised part_volume_scale over per-sample controller arrays -> per-sample gain (1.0 at
       127/127/127). Same integer law as E.part_volume_scale, ZOH at block resolution."""
    a = np.rint(cc7).astype(np.int64); b = np.rint(cc11).astype(np.int64); m = np.rint(master).astype(np.int64)
    def pv(aa, bb, mm):
        t = (((bb*aa) & 0xffff) * mm >> 6) & 0xffff
        return (t * 0x10410 >> 16) ** 2
    return pv(a, b, m).astype(np.float64) / float(pv(127, 127, 127))

def render_script(path, out="our_seq.wav", tail=2.2, tone_map=4, verbose=True):
    return render_events(parse_script(path), tail=tail, out=out, tone_map=tone_map, verbose=verbose)

# ----------------------------------------------------------------- Standard MIDI File
def parse_smf(path):
    """Minimal dependency-free SMF reader -> our event list (pos in samples on the 32-grid).
       Handles format 0/1/2, running status, tempo-map (meta 0x51), sysex; converts ticks->seconds
       through the tempo map, then quantises to the render-block grid the engine applies events on."""
    d = open(path, 'rb').read()
    assert d[:4] == b'MThd', "not a MIDI file"
    fmt, ntrk, div = struct.unpack('>HHH', d[8:14])
    assert not (div & 0x8000), "SMPTE division not supported"
    tpq = div                                              # ticks per quarter note
    p = 14
    tracks = []                                            # each: list of (abs_tick, kind, payload)
    for _ in range(ntrk):
        assert d[p:p+4] == b'MTrk'
        ln = struct.unpack('>I', d[p+4:p+8])[0]; p += 8
        end = p + ln; tick = 0; status = 0; evs = []
        while p < end:
            dt, p = _varlen(d, p); tick += dt
            b = d[p]
            if b & 0x80: status = b; p += 1
            else: b = status                               # running status
            if b == 0xFF:                                  # meta
                mt = d[p]; p += 1; mlen, p = _varlen(d, p); data = d[p:p+mlen]; p += mlen
                if mt == 0x51: evs.append((tick, 'tempo', struct.unpack('>I', b'\0'+data)[0]))
            elif b in (0xF0, 0xF7):                          # sysex
                mlen, p = _varlen(d, p); data = d[p:p+mlen]; p += mlen
                if b == 0xF0: evs.append((tick, 'sx', bytes([0xF0]) + data))
            else:                                           # channel voice
                hi = b & 0xF0
                if hi in (0xC0, 0xD0):
                    evs.append((tick, 'm', b, d[p], 0)); p += 1
                else:
                    evs.append((tick, 'm', b, d[p], d[p+1])); p += 2
        tracks.append(evs)
    # merge tracks, stable by tick, then walk the tempo map to assign absolute seconds
    merged = sorted((e for tr in tracks for e in tr), key=lambda e: e[0])
    events = []; cur_tempo = 500000; last_tick = 0; sec = 0.0
    for e in merged:
        sec += (e[0] - last_tick) * (cur_tempo / 1e6 / tpq); last_tick = e[0]
        if e[1] == 'tempo': cur_tempo = e[2]
        elif e[1] == 'sx':  events.append((_q(sec * SR), 'sx', e[2]))
        else:               events.append((_q(sec * SR), 'm', e[2], e[3], e[4]))
    events.sort(key=lambda e: e[0])
    return events

def _varlen(d, p):
    v = 0
    while True:
        b = d[p]; p += 1; v = (v << 7) | (b & 0x7f)
        if not (b & 0x80): return v, p

def render_smf(path, out="our_seq.wav", tail=2.2, tone_map=4, verbose=True,
               reverb=False, chorus=False, delay=False, rev_type=None, cho_type=None, dly_type=None):
    return render_events(parse_smf(path), tail=tail, out=out, tone_map=tone_map, verbose=verbose,
                         reverb=reverb, chorus=chorus, delay=delay,
                         rev_type=rev_type, cho_type=cho_type, dly_type=dly_type)

if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1:
        render_script(sys.argv[1], out=sys.argv[2] if len(sys.argv) > 2 else "our_seq.wav")
