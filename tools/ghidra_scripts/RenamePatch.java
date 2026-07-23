import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the patch/tone/multisample/wave layer (agent-traced, wave table validated) + engine helpers. */
public class RenamePatch extends GhidraScript {
    String[][] F = {
        // engine helpers (my analysis)
        {"1800887c0","get_local_time_ms","GetLocalTime -> ms since midnight; timestamps incoming MIDI."},
        {"180090b74","alloc_retry","operator-new style malloc with _callnewh retry."},
        {"18008f020","tg_start_pending_voices","Per-block: each part (stride 0x488) with non-empty partial list (+0x270) -> part_start_voices."},
        {"18008aca0","tg_output_filter","Per-block output biquad (state @DAT_181a6e4a8). Called by TG_Process. [likely]"},
        {"18008a6c0","voice_pool_scan","Scans voice pool (g_voice_run_flags) for alloc/priority. [likely]"},
        {"180089a10","voice_compute_mod_rates","Packed per-voice rate params -> float rates; from voice_setup. [likely]"},
        {"18008b510","voice_set_ramp_target_0","Per-voice control-ramp target write. [likely]"},
        {"18008b660","voice_set_ramp_target_1","Per-voice control-ramp target write. [likely]"},
        {"18008b790","voice_set_ramp_target_2","Per-voice control-ramp target write. [likely]"},
        // patch/tone/wave layer (agent-traced; wave table VALIDATED)
        {"18005ec90","wavedesc_decode","Decodes a 0x16-byte wave descriptor -> partial+0x1a0 {loop,end,start,wave_ctrl}. 20-bit region-relative loop/end/start; byte[0]&0x7f=region, byte[0xa] flags(bit0 fmt/bit1 loop/bit2 reverse)."},
        {"18005fc20","partial_compute_pitch","Pitch from wave root(byte6*1000)-fine + coarse tune + key-follow curve + master/bend."},
        {"1800026d0","tone_lookup","tone# (part+0x232, <0x4000 melodic) -> tone struct: melodic table g_tone_table_melodic stride 0x100 (0x24 hdr + 2*0x6e partials); drum g_tone_table_drum stride 0x1e8."},
        {"180003c80","tone_resolve","Resolves tone struct/partial-param blocks (companion to tone_lookup). [likely]"},
        {"180003420","multisample_select_wave","key -> wave#: multisample table (stride 0x8c) key-split bounds@+0x0c, wave#@+0x2c[zone], vel-alts@+0x2a/+0x2e; returns wave#*0x16 + wavedesc base."},
        {"1800031f0","multisample_key_zone","Key -> zone index + pitch key-follow/crossfade weight (partial+0x16a)."},
        {"180003e90","partial_velocity_gate","Velocity range gate (block+0x4f velLo/+0x51 velHi) + vel crossfade level; clears partial bit if out of range."},
        {"180003a60","tone_apply_velocity_splits","Per-partial velocity gating; initial mask from tone-common+0x24 (8-byte partial-enable bitmask)."},
        {"180002f30","partial_build","Per-partial: key-zone -> multisample -> wave# -> descriptor, staged into partial. [likely]"},
        {"1800029e0","partial_alloc_node","Allocates partial node; sets partial+0x138 (wavedesc ptr)/+0x148 (0x6e param block)/+0x158 (multisample). [likely]"},
        {"18005ee30","partial_load_params","Copies 0x16 wavedesc + 0x6e param block into per-voice work; calls wavedesc_decode; sets TVA."},
        {"180061210","partial_compute_filter","TVF: cutoff base(block+0x31)->table, key-follow, resonance(block+0x32), env depth(block+0x33)."},
    };
    String[][] L = {
        {"181a18f00","g_tone_table_melodic"},  {"181a18f08","g_multisample_table_a"},
        {"181a18f10","g_wave_desc_table_a"},   {"181a18ef8","g_tone_set_header_a"},
        {"181a1f000","g_tone_set_header_b"},   {"181a1f008","g_tone_table_drum"},
        {"181a1f010","g_multisample_table_b"}, {"181a1f018","g_wave_desc_table_b"},
        {"181897b40","g_wave_desc_src_a"},     {"1818ca570","g_multisample_src_a"},
        {"1818f2810","g_tone_table_src_a"},
    };
    @Override public void run() throws Exception {
        int ok=0,lb=0;
        for (String[] e : F){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); continue; }
            fn.setName(e[1],SourceType.USER_DEFINED); if(e.length>2&&!e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }
        println("RenamePatch: "+ok+" funcs, "+lb+" labels.");
    }
}
