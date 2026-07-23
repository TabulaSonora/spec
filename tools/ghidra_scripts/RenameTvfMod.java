import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the TVF state-variable filter runner + the mod-wheel/LFO apply functions that were still
 *  FUN_ after the LFO-depth, TVF-coefficient and mod-wheel RE passes (2026-07). */
public class RenameTvfMod extends GhidraScript {
    String[][] F = {
        {"18008d9a0","tvf_svf_render",
         "TVF Chamberlin state-variable filter, 4-wide SIMD. f = g_svf_f_coef[v] (DAT_181a1cb70), "
         + "q = g_svf_q_coef[v] (DAT_181a1d1f0). Per sample: low += f*band; high = in - (q*band + low); "
         + "band += f*high. Taps: lp=low, hp=high, bp=band, notch=low-high. f from voice+0xcc via "
         + "2^(cc/16384-15) (voice_set_ramp_target_2 + g_ramp_exp_tbl); q from voice+0xdc/131072."},
        {"18008ce70","tvf_svf_render_alt",
         "Alternate TVF filter runner selected alongside tvf_svf_render at the runner-select (~L78199)."},
        {"1800819c0","mod_pitch_control",
         "Mod-wheel MOD PITCH CONTROL: part+0x420 (bipolar around 0x40) scaled by CC1 -> semitone "
         + "pitch offset, summed into the pitch accumulator part+0x3a2. Separate from the LFO1 pitch-"
         + "depth path (lfo_update/lfo_apply_depth); this is the direct mod->pitch-bend term."},
        {"180084350","tvf_cutoff_add_lfo",
         "Adds the LFO TVF-destination modulation to the runtime cutoff (voice+0xcc) then clamps to "
         + "the 15-bit cutoff range. The engine ADDS then clamps (not clamp-then-add)."},
        {"180060390","voice_volume_apply",
         "Voice/part volume: vol = (partLevel*expression*master)>>6; for a drum part (voice+0x158!=0) "
         + "vol = (kit_level*vol)>>7 (linear); then the part gain is SQUARED (vol>>16)^2. Also folds "
         + "the TVA-destination LFO in as vol' = vol + vol*mod/0x7f00 (mod clamped +-0x7f00)."},
        {"1800830e0","lfo_pitch_accumulate",
         "Adds the summed LFO pitch modulation (milli-semitones, 1000 = 1 semitone) into the voice "
         + "pitch accumulator, clamped to 0x1f018 = 127000 = 127 semitones."},
    };
    String[][] L = {
        {"181a1cb70","g_svf_f_coef"},          // per-voice SVF frequency coefficient scratch (SoA)
        {"181a1d1f0","g_svf_q_coef"},          // per-voice SVF damping/Q coefficient scratch (SoA)
        {"181986420","g_ramp_exp_tbl"},        // 257 x int32, T[i]=2^17*2^(i/256); f = 2^(cc/16384-15)
        {"181986860","g_svf_makeup_gain_tbl"}, // resonance-indexed filter makeup-gain LIMITER (float)
        {"181a70e60","g_svf_cutoff_log_to"},   // SoA snapshot of voice+0xcc (log cutoff target)
    };
    @Override public void run() throws Exception {
        int ok=0, miss=0, lb=0;
        for (String[] e : F){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); miss++; continue; }
            fn.setName(e[1],SourceType.USER_DEFINED);
            if(e.length>2 && !e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }
        println("RenameTvfMod: "+ok+" funcs renamed, "+miss+" missing, "+lb+" labels.");
    }
}
