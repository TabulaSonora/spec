#!/usr/bin/env python
"""Generate tables/manifest.json: the map from every cached table (and the live-read ROM/directory
regions) to its byte-exact location inside SCCore.dll. Run from the repo root. The manifest lets a
downstream implementer rebuild the whole engine's static data from just the (version-pinned) DLL --
no reliance on the tables/*.bin cache, which is itself derivable from these offsets.

Usage: python tools/gen_manifest.py [path-to-SCCore.dll]
"""
import os, sys, json, re, struct, hashlib

DLLPATH = sys.argv[1] if len(sys.argv) > 1 else r"C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, "tables")
IMAGEBASE = 0x180000000
dll = open(DLLPATH, "rb").read()

def pe_timestamp(b):
    """PE COFF TimeDateStamp (unix seconds) -- a stable build identifier."""
    e_lfanew = struct.unpack_from("<I", b, 0x3C)[0]
    if b[e_lfanew:e_lfanew+4] != b"PE\0\0":
        return None
    return struct.unpack_from("<I", b, e_lfanew + 8)[0]

# curated metadata: file -> (symbol, dtype, shape, subsystem, purpose)
META = {
 "curve_level_2a00.bin":("g_level_curve","u16","256","TVA","envelope stage byte -> 16-bit log attenuation"),
 "curve_amp_hi_2ba0.bin":("g_amp_curve_hi","u16","256","TVA","amplitude lookup, high half (amp_of)"),
 "curve_amp_lo_2da0.bin":("g_amp_curve_lo","u16","256","TVA","amplitude lookup, low half (amp_of)"),
 "kf_tvalevel_026a0.bin":("g_kf_tvalevel","u8","128x128 (row*0x80+key)","TVA","TVA base-level key-follow"),
 "kf_tvalevel2_01fa0.bin":("g_kf_tvalevel2","u8","128x128","TVA","TVA level key-follow (second term)"),
 "kf_tvarate0_023a0.bin":("g_kf_tvarate0","u8","128x128","TVA","TVA env rate key-follow, segments (block 0x65)"),
 "kf_tvarate1_020a0.bin":("g_kf_tvarate1","u8","128x128","TVA","TVA env rate key-follow, release (block 0x66)"),
 "curve_vel_2b00.bin":("g_vel_curve","u8","256","TVA","velocity response curve"),
 "curve_segrate_2900.bin":("g_rate_curve","u16","128","ENV","rate byte -> segment time (ms-ish)"),
 "curve_rateout_3060.bin":("g_env_rate_out","u16","512","ENV","env_rate_scale output curve (8.8 mult)"),
 "curve_scale_28e8.bin":("g_env_scale_curve","u8","256","ENV","env rate/level scale curve (0x40-neutral mod)"),
 "env_shape_7a90.bin":("g_env_shape","u16","256 (0x200 used)","ENV","fast-approach segment shape (env_ramp_segment)"),
 "env_startphase_7a30.bin":("g_env_startphase","u16","128","ENV","per-segment initial phase seed"),
 "curve_filtenvdepth_2f00.bin":("g_tvf_env_depth","u8/u16","352","TVF","TVF env-depth conversion (tvf_env_level_conv)"),
 "kf_tvfenv_02a20.bin":("g_kf_tvfenv","s16","128x128 (rows 0-15 used)","TVF","TVF env-depth key-follow (block 0x32 nibble)"),
 "curve_reso_2b88.bin":("g_reso_curve","s16","64","TVF","resonance curve (block 0x4a)"),
 "kf_tvfrate_03920.bin":("g_kf_tvfrate","u8","128x128","TVF","TVF env rate key-follow (main 0x46/0x48; release row0 == g_kf_tvfrate2)"),
 "curve_filttype_987b00.bin":("g_filter_type_coef","u32","16","TVF","filter TYPE byte (block 0x31) -> tap/coef word"),
 "tvf_warp_a83d0.bin":("g_tvf_cutoff_warp","u16","129","TVF","cutoff warp (stage C, FUN_180084470)"),
 "tvf_ceil_a7ed0.bin":("g_tvf_cutoff_ceil","u16","128","TVF","cutoff ceiling by resonance byte"),
 "tvf_q_lp_a7cd0.bin":("g_tvf_q_lp","u16","256","TVF","Q trim, filter type 0 at neutral reso"),
 "tvf_q_t6_a7fd0.bin":("g_tvf_q_t6","u16","256","TVF","Q trim, filter type 6"),
 "ramp_exp_986420.bin":("g_ramp_exp_tbl","i32","257","TVF","2^17*2^(i/256) exp decode for the SVF f-coefficient"),
 "curve_pitchenv_2578.bin":("g_pitch_env","u8/u16","856","PITCH","pitch env conversion region (incl DAT_1819a2890 sub-table)"),
 "curve_pitchbias_2890.bin":("g_pitch_bias","u8","128","PITCH","pitch env bias sub-table (DAT_1819a2890)"),
 "curve_pitchdepthvs_28d0.bin":("g_pitch_depth_vs","u16","64","PITCH","pitch env depth vs velocity"),
 "kf_pitchrate0_01f20.bin":("g_kf_pitchrate0","u8","128x128","PITCH","pitch env rate key-follow, segments (block 0x27/0x29)"),
 "kf_pitchrate1_01aa0.bin":("g_kf_pitchrate1","u8","128x128","PITCH","pitch env rate key-follow, release (block 0x27/0x2a)"),
 "kf_pitch_01b20.bin":("g_kf_pitch","s16","8x128 (row*0x80+key)","PITCH","pitch key-follow (row=(block[0x13]-0x40)>>2)"),
 "lfo_wave_1740.bin":("g_lfo_wave","u8","128 used","LFO","half-sine LFO waveform (sel 0)"),
 "lfo_rate_2790.bin":("g_lfo_rate_tbl","u16","128","LFO","LFO1 rate byte -> 16-bit phase increment"),
 "lfo_cents_2690.bin":("g_lfo_cents_tbl","u16","128","LFO","LFO pitch depth byte -> cents"),
 "lfo_wavebank_a17f0.bin":("g_lfo_wavebank","i16","24x0x81","LFO","LFO wavetables 8..0x1f"),
 "lfo_wavemap_87ae0.bin":("g_lfo_wavemap","u8","32","LFO","waveform selector nibble -> table index"),
 "lfo_delay_a2590.bin":("g_lfo_delay_tbl","u16","128","LFO","LFO1 delay byte -> delay ticks"),
 "interp_coef_a0f210.bin":("g_interp_coef_table","f32","128x4","SAMPLER","4-tap FIR resample kernel (phase>>9)"),
 "pan_a2fa1.bin":("g_pan_tbl","u8","128","MIX","pan position -> L/R gain"),
 "curve_velsens_01520.bin":("g_vel_sens","u16","1024","TVA","velocity sensitivity curve"),
 "curve_velxfade_01020.bin":("g_vel_xfade","u16","1024","DIR","multisample velocity-crossfade curve"),
 "ramp_divider_a03e00.bin":("g_ramp_divider","u8","16","CTRL","anti-zipper ZOH masks [0x00,0x07,0x1F,0x7F]"),
 "ramp_flagword_a84d8.bin":("g_ramp_flagword","u8","16","CTRL","per-destination ramp-divider selector bits"),
 "lut1_2e30.bin":("g_dir_lut1","u8","128","DIR","program/tone directory LUT 1"),
 "lut2_28b0.bin":("g_dir_lut2","u8/u16","16384","DIR","program/tone directory LUT 2"),
 "lut3_32b0.bin":("g_dir_lut3","u16","32768","DIR","program/tone directory LUT 3 (tone# lookup)"),
 # 2363 records, not the round 2048 the first slice took. Nothing in the engine bounds this table --
 # tone_lookup tests only tone# < 0x4000 and indexes -- so the length is a layout fact: records read
 # as tone records through 2362, 2363 is not one, and the next known object starts at 0x1985420.
 # Drum kits reach tone 2353, so a short slice silences them. See FINDINGS, "The tone table is 2363".
 "tone_a.bin":("g_tone_table","bytes","0x100 stride x2363","DIR","tone records (header 0x24 + 4 partial 0x6e blocks)"),
 "multisample_a.bin":("g_multisample_table","bytes","0x8c stride","DIR","multisample key/vel zone -> wave#"),
 # 4259 records, and 4096 was the same round-number mistake as the tone table one level down. The
 # multisamples a defined tone reaches name waves up to 4258, and 4259 records end at 0x18189ad942 --
 # fourteen bytes below g_drum_kits at 0x18189ad950, so the layout leaves room for no more. The
 # engine's own captured wave selections agree: 2022 of 2022 forward waves resolve, against 2014 on
 # the short slice. See FINDINGS, "The wave descriptor table is 4259 records".
 "wavedesc_a.bin":("g_wavedesc_table","bytes","0x16 stride x4259","DIR","wave descriptors: ROM coords, root key, loop"),
 "layered_1896690.bin":("g_layered_table","bytes","0x18 stride x50","DIR","layered / alternate-articulation records"),
}

def find_off(data):
    idx = dll.find(data)
    if idx >= 0:
        return idx, "full"
    for pre in (0x1000, 0x800, 0x200):     # over-read caches (e.g. kf_tvfenv): match the used prefix
        if len(data) > pre:
            p = dll.find(data[:pre])
            if p >= 0:
                n = 0
                while n < len(data) and p + n < len(dll) and dll[p + n] == data[n]:
                    n += 1
                return p, "prefix %d/%d" % (n, len(data))
    return -1, "NOT FOUND"

def va_for(off, name):
    """The filename hex suffix encodes the low bits of the VA; pick the section adjust that matches."""
    m = re.search(r'_([0-9a-fA-F]{4,6})\.bin$', name)
    for adj in (0x1000, 0x1400):
        va = off + adj + IMAGEBASE
        if m and (va & ((1 << (4 * len(m.group(1)))) - 1)) == int(m.group(1), 16):
            return va, adj
    return None, None

entries = []
for name in sorted(os.listdir(T)):
    if not name.endswith(".bin"):
        continue
    data = open(os.path.join(T, name), "rb").read()
    off, how = find_off(data)
    sym, dt, shape, sub, purpose = META.get(name, ("?", "?", "?", "?", "(undocumented)"))
    va, adj = va_for(off, name) if off >= 0 else (None, None)
    entries.append(dict(name=name, symbol=sym, subsystem=sub, dtype=dt, shape=shape,
                        size=len(data), file_offset=hex(off) if off >= 0 else None,
                        va=hex(va) if va else None, section_adjust=hex(adj) if adj else None,
                        match=how, purpose=purpose))

live = [
 dict(name="wave_rom_bank_A", symbol="g_wave_rom[0]", subsystem="ROM", dtype="ADPCM block-FP",
      file_offset=hex(0x92700), size=0xC00000,
      purpose="wave ROM bank A (stacked SC-88/88Pro); 1MB blocks, delta+scale streams"),
 dict(name="wave_rom_bank_B", symbol="g_wave_rom[1]", subsystem="ROM", dtype="ADPCM block-FP",
      file_offset=hex(0x1092730), size=0xC00000,
      purpose="wave ROM bank B (8820); 1MB blocks"),
 dict(name="drum_kit_records", symbol="g_drum_kits", subsystem="DIR", dtype="bytes",
      va=hex(0x18AD950), purpose="drum kit records, 0x50C stride; note-indexed tone#/level/coarse planes"),
 dict(name="drum_bank_row", symbol="DAT_181a00bb0", subsystem="DIR", dtype="u8",
      va=hex(0x19FFBB0), purpose="internal-bank code -> drum map row"),
 dict(name="drum_prog_map", symbol="DAT_1819f2eb0", subsystem="DIR", dtype="u8",
      va=hex(0x19F1EB0), purpose="[row*0x80+program] -> kit index (0xFF=undefined)"),
]

manifest = dict(
  _note=("Every static table SC-VA needs lives inside SCCore.dll. The tables/*.bin files are "
         "byte-for-byte slices of the DLL, fully derivable from the offsets below -- the repo ships "
         "them only so it need not redistribute the copyrighted DLL. Verify the DLL identity first."),
  dll=dict(
    filename="SCCore.dll",
    product="Roland VS Sound Canvas VA (version-specific)",
    size=len(dll),
    sha256=hashlib.sha256(dll).hexdigest(),
    sha1=hashlib.sha1(dll).hexdigest(),
    md5=hashlib.md5(dll).hexdigest(),
    pe_timestamp=pe_timestamp(dll),
    note="version resource is empty; identify by hash + PE timestamp + size",
  ),
  image_base=hex(IMAGEBASE),
  va_to_file_offset={
    ".rdata curve/kf tables": "file_offset = VA - 0x180000000 - 0x1000",
    "interp-coef section":    "file_offset = VA - 0x180000000 - 0x1400",
    "data section (ROM/directory)": "own base offsets, listed per live_region",
  },
  cached_tables=entries,
  live_regions=live,
)

out = os.path.join(T, "manifest.json")
open(out, "w").write(json.dumps(manifest, indent=2))
nf = [e["name"] for e in entries if e["file_offset"] is None]
nova = [e["name"] for e in entries if e["va"] is None and e["file_offset"]]
print("tables=%d  not_found=%d  no_VA(data-section/other)=%d" % (len(entries), len(nf), len(nova)))
if nf:   print("  NOT FOUND:", nf)
if nova: print("  offset but no VA match:", nova)
print("dll sha256:", manifest["dll"]["sha256"])
print("wrote", out)
