#!/usr/bin/env python
"""
Fully-static Sound Canvas VA patch directory, reconstructed from the ROM tables
(tone / multisample / wave-descriptor) exported from SCCore.dll. NO ENGINE.
Resolves (tone#, note, velocity) -> the wave(s) that sound, with ROM coordinates.
"""
import struct, os
T = os.path.join(os.path.dirname(__file__), "tables")
tone_b  = open(os.path.join(T,"tone_a.bin"),"rb").read()
msmp_b  = open(os.path.join(T,"multisample_a.bin"),"rb").read()
wdsc_b  = open(os.path.join(T,"wavedesc_a.bin"),"rb").read()
layr_b  = open(os.path.join(T,"layered_1896690.bin"),"rb").read()   # stride 0x18, 50 entries

def u8(b,o): return b[o]
def u16(b,o): return b[o] | (b[o+1]<<8)
def s16(b,o):
    v=u16(b,o); return v-0x10000 if v>=0x8000 else v

# ---- wave descriptor (stride 0x16) ----
def wave(n):
    o=n*0x16
    d=wdsc_b[o:o+0x16]
    if len(d)<0x16: return None
    region=d[0]&0x7f
    loop =((d[1]&0xf)<<16)|(d[2]<<8)|d[3]
    end  =((d[7]&0xf)<<16)|(d[8]<<8)|d[9]
    start=((d[0xb]&0xf)<<16)|(d[0xc]<<8)|d[0xd]
    flags=d[0xa]
    bank = (flags & 1)  # format/bank bit — refine if needed
    return dict(region=region, loop=loop, end=end, start=start,
                root=d[6], fine=u16(d,4), flags=flags,
                reverse=(flags>>2)&1)

# ---- multisample (stride 0x8c): key -> wave# ----
def msamp_wave(ms, key):
    o=ms*0x8c
    d=msmp_b[o:o+0x8c]
    if len(d)<0x8c: return None
    # walk key-split upper bounds at +0x0c until *p >= key
    zone=0
    for z in range(0x20):
        bound=d[0x0c+z]
        if key<=bound: zone=z; break
        if bound>=0x7f: zone=z; break
    else:
        zone=0x1f
    w=s16(d,0x2c+zone*2)
    # EMPTY BOTTOM ZONE = below the multisample's sampled range -> this partial does not sound.
    # Verified live: Slap Bass 2 p0 (ms#196: zone0 keys 0-39 wave=-1, zone1 40+ wave=1099) is
    # allocated at note 40+ but produces NO voice at note <=39 (the exact zone-0 boundary). Although
    # multisample_select_wave statically down-extrapolates (+0x2e) into zone 0, the resulting voice is
    # silent/terminated, so the audible result is: empty zone 0 -> partial silent. (56/1883 multisamples
    # have an empty bottom zone.) Do NOT fall through to the neighbour wave here.
    if w<0 and zone==0:
        return None
    if w<0:  # empty non-bottom zone (top-extrapolate / vel-layer alt)
        w=s16(d,0x2e+zone*2)
    if w<0:
        w=s16(d,0x6a)  # fallback
    return w if w>=0 else None

# ---- tone (stride 0x100): name + 2 partials of 0x6e at +0x24 ----
def tone(n):
    o=n*0x100
    d=tone_b[o:o+0x100]
    if len(d)<0x100: return None
    name=bytes(d[0:12]).decode('latin1').rstrip(' \x00')
    parts=[]
    for pi in range(2):
        b=0x24+pi*0x6e
        ms=u16(d,b+2)
        if ms==0xffff: continue
        parts.append(dict(msamp=ms, keyCenter=d[b+4], keyTranspose=d[b+0x10],
                          velLo=d[b+0x4f], velHi=d[b+0x51], level=d[b+0x50]))
    return dict(name=name, partials=parts)

def tone_level(n):
    """Per-tone master level, tone header +0x0c (right after the 12-char name).
       The engine copies it to voice+0x167 (partial_load @180001522: `voice+0x167 =
       *(byte*)(voice+0x150 /*tone base*/ + 0xc)`) where tva_compute_base_level
       subtracts g_level_curve[it] from the TVA base."""
    return tone_b[n*0x100 + 0x0c]

def zone_level(ms, key, keycenter):
    """Per-KEY-ZONE level from the multisample block, +0x6c[zone] (0x20 bytes, parallel to
       the key-split bounds at +0x0c). multisample_select_wave @180003420 returns it through
       its out-param, which partial_load stores at voice+0x140 -- the second attenuation in
       tva_compute_base_level. The +0x8b / +0x6b / +0x6d variants below mirror the same
       fallbacks the engine uses when a zone has no direct wave."""
    o=ms*0x8c; d=msmp_b[o:o+0x8c]
    if len(d)<0x8c: return 127
    tk=max(0, min(0x7f, key + (0x40-keycenter)))
    if d[0x2b] < tk: return d[0x8b]              # past the last bound: the tail entry
    z=0
    while d[0x0c+z] < tk: z+=1
    if s16(d,0x2c+z*2) < 0:                      # no direct wave -> velocity-alternate plane
        return d[0x6d+z] if z==0 else (d[0x6b+z] if d[0x0c+z]==0x7f else 127)
    return d[0x6c+z]

def bank_of(coords):
    """ROM bank (0=A,1=B) from the descriptor region bit 4."""
    return (coords['region'] >> 4) & 1

def multisample_zones(ms):
    """List of (tk_lo, tk_hi, wave#) transposed-key zones for a multisample."""
    o=ms*0x8c; d=msmp_b[o:o+0x8c]
    if len(d)<0x8c: return []
    zones=[]; prev=0
    for z in range(0x20):
        bound=d[0x0c+z]
        w=s16(d,0x2c+z*2)
        if w<0: w=s16(d,0x2e+z*2)
        if w>=0: zones.append((prev, bound, w))
        prev=bound+1
        if bound>=0x7f: break
    return zones

def tone_zones(tone_no):
    """Full static zone map for a tone: per-partial (velocity layer) key zones -> wave + coords.
       Key bounds converted from transposed-key space back to MIDI notes."""
    tn=tone(tone_no)
    if not tn or len(tn['name'])<2: return None
    out=[]
    for pi,p in enumerate(tn['partials']):
        kc=p['keyCenter']; vlo,vhi=sorted((p['velLo'],p['velHi']))
        for tk_lo,tk_hi,w in multisample_zones(p['msamp']):
            nlo=max(0, tk_lo+kc-0x40); nhi=min(127, tk_hi+kc-0x40)
            if nhi<nlo: continue
            c=wave(w)
            if not c: continue
            out.append(dict(partial=pi, velLo=vlo, velHi=vhi, keyLo=nlo, keyHi=nhi,
                            wave=w, region=c['region'], bank=bank_of(c),
                            reverse=c['reverse'], loop=c['loop'], end=c['end'], start=c['start']))
    return tn['name'], out

# ---- program/bank/map -> tone# (validated 25/25 vs engine) ----
_lut1 = open(os.path.join(T,"lut1_2e30.bin"),"rb").read()
_lut2 = open(os.path.join(T,"lut2_28b0.bin"),"rb").read()
_lut3 = open(os.path.join(T,"lut3_32b0.bin"),"rb").read()
def _lut3_raw(program, tone_map, bank):
    """The raw UNSIGNED u16 the 3-level LUT yields. 0xffff = unassigned. None if a level is 0xff."""
    if not (0 <= tone_map < len(_lut1)): return None
    g1=_lut1[tone_map]
    if g1==0xff: return None
    i2=g1*0x80+bank
    if i2>=len(_lut2) or _lut2[i2]==0xff: return None
    o=(_lut2[i2]*0x80+program)*2
    if o+2>len(_lut3): return None
    return struct.unpack_from("<H",_lut3,o)[0]

# The u16 has THREE tone spaces, per the dispatch in FUN_18006c7ba / tone_lookup @1800011f0:
#     v < 0x4000            melodic, g_tone_table_melodic[v]          (stride 0x100)
#     0x4000 <= v < 0x6000  drum,    g_tone_table_drum[v-0x4000]      (stride 0x1e8)
#     0x6000 <= v < 0x8000  LAYERED, DAT_181a18f20[v-0x6000]          (stride 0x18)
#     v >= 0x8000           not directly selectable (see below)
# An ALT entry is 10 chars of name + ": " + two (map,bank,program) triples at +0x10 and +0x14, each
# re-resolved through the SAME 3-level LUT. The program-change handler FUN_180068fe0 masks those
# second-pass results with & 0x7fff -- the 0x8000 bit marks a tone that exists but is reachable only
# through such an entry, never by a direct program change. That is why the four string patches
# looked "unassigned": we read the u16 as signed and bailed on the sign bit.
#
# The two triples are NOT layers played together. Triple 1 is the tone (part+0x232); triple 2 is a
# conditional ALTERNATE ARTICULATION (part+0x234), named ":L" in the ROM. FUN_180003d42 picks it
# over the primary only when ALL of: (part+0x12 & 0x20) == 0, part+0x3d9 < 0 (mono/solo mode),
# FUN_180068d70(part) >= 2, and the elapsed-time test `part+0x237 <= now - part+0x238` where the
# threshold part+0x237 comes from this entry's +0x0e. In the ordinary poly path it takes the
# LAB_180003d2d branch and the alternate never sounds. CONFIRMED against the engine: prog 48 holds
# exactly 2 voices whose zone levels (120, 127) are the two partials of the PRIMARY tone 390 alone.
ALT_STRIDE = 0x18

def alt_entry(idx):
    """Decode entry `idx` of the alternate-articulation table (tone# 0x6000+idx)."""
    o=idx*ALT_STRIDE; e=layr_b[o:o+ALT_STRIDE]
    if len(e)<ALT_STRIDE: return None
    return dict(name=e[:10].decode('latin1').rstrip(), thresh=e[0x0e],
                refs=[(e[b], e[b+1], e[b+2]) for b in (0x10, 0x14)])   # (map, bank, program)

def _deref(ref):
    m,bk,pg = ref
    w=_lut3_raw(pg, m, bk)
    if w is None or w==0xffff: return -1
    w &= 0x7fff                                     # strip the "indirect-only" marker
    return w if w < 0x4000 else -1

def program_tones(program, tone_map=4, bank=0):
    """(map, bank MSB, program) -> the list of tone#s that SOUND for a plain poly note.
       Always 0 or 1 entries; kept as a list because render_note walks it. See alt_tone() for the
       mono/solo alternate articulation, which is NOT played simultaneously."""
    v=_lut3_raw(program, tone_map, bank)
    if v is None or v==0xffff or v>=0x8000: return []
    if v < 0x6000: return [v]                       # melodic or drum, direct
    ent=alt_entry(v-0x6000)
    if ent is None: return []
    t=_deref(ent['refs'][0])
    return [t] if t >= 0 else []

def alt_tone(program, tone_map=4, bank=0):
    """The mono/solo alternate-articulation tone# for a program, or -1. Not currently rendered --
       reproducing it needs the part's mono-mode flag and the inter-note timing test."""
    v=_lut3_raw(program, tone_map, bank)
    if v is None or v==0xffff or v>=0x8000 or v < 0x6000: return -1
    ent=alt_entry(v-0x6000)
    return _deref(ent['refs'][1]) if ent else -1

def program_to_tone(program, tone_map=4, bank=0):
    """(map, bank MSB, program) -> tone#. map: 1=SC55 2=SC88 3=SC88Pro 4=SC8820. -1 if unassigned.
       For a layered patch this returns only the FIRST tone -- use program_tones() for all of them."""
    ts=program_tones(program, tone_map, bank)
    return ts[0] if ts else -1

def resolve_midi(program, note, vel, tone_map=4, bank=0):
    """Full MIDI-addressed static resolve: (map,bank,program,note,vel) -> (tone name, waves).
       Only the first tone of a layered patch; render_note walks program_tones() itself."""
    t=program_to_tone(program, tone_map, bank)
    if t<0: return ("(unassigned)", [])
    return resolve(t, note, vel)

def resolve(tone_no, note, vel):
    """Return list of (partial_index, wave#, wave_coords) that sound."""
    tn=tone(tone_no)
    if not tn or len(tn['name'])<2: return ("(none)", [])
    out=[]
    for pi,p in enumerate(tn['partials']):
        lo,hi=sorted((p['velLo'],p['velHi']))
        if not (lo<=vel<=hi): continue
        # The zone lookup uses the TRANSPOSED key. multisample_select_wave @180003420 is handed
        # the voice's key (which partial_compute_pitch has already shifted by block[0x10]) and
        # only then adds (0x40 - keyCenter) itself. Missing the transpose picks a wave from the
        # wrong zone, which the pitch chain then has to stretch to compensate: Jetplane's partial 1
        # (+24 st) selected wave 215 (root 60) and played it at 4x -- audible aliasing -- where the
        # engine selects wave 217 (root 84) from the transposed key and plays it at 0.98x.
        key = note + (p['keyTranspose'] - 0x40) + (0x40 - p['keyCenter'])
        key = max(0,min(0x7f,key))
        w = msamp_wave(p['msamp'], key)
        if w is None: continue
        out.append((pi, w, wave(w)))
    return tn['name'], out

if __name__=="__main__":
    import sys
    # validate tone#0 "Piano 1" across a few notes
    for tno in [0]:
        for note in [36,48,60,72,84]:
            name, ws = resolve(tno, note, 100)
            desc=", ".join(f"P{pi} w#{w} r{c['region']} loop{c['loop']}" for pi,w,c in ws)
            print(f"tone#{tno} '{name}' note {note}: {desc}")
