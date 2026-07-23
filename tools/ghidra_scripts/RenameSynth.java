import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the per-partial synthesis "back half" (TVA / TVF / pitch env / LFO) + curve tables. */
public class RenameSynth extends GhidraScript {
    String[][] F = {
        // --- shared converters (confident) ---
        {"1800607e0","env_rate_scale","Envelope RATE modifier: (baseRate, param 0x40=neutral) -> 8.8 fixed rate mult via g_env_scale_curve + g_env_rate_out. Returns 0x100 (=1.0) at neutral."},
        {"180060880","env_level_scale","Level/depth scaler: two 0x40-centered params -> 8.8 fixed mult via g_env_scale_curve + g_env_rate_out."},
        {"180061640","tvf_env_level_conv","Converts a TVF env stage level byte -> internal filter env amplitude (per-voice)."},
        {"18008fbb0","lfo_value","Returns current LFO output (u16); source for pitch/TVF/TVA modulation."},
        // --- TVA amplitude (confident) ---
        {"180060960","tva_compute_base_level","TVA base level from patch level(block+0x53), level key-follow(block+0x54/+0x55, g_kf_tvalevel), velocity(g_vel_curve) -> DAT_181a1f5a8."},
        {"180060b00","tva_compute_env_levels","4 TVA env segment target amplitudes: base(DAT_181a1f5a8) - per-stage level(block+0x5a..+0x5d, g_level_curve) mapped via g_amp_curve_hi/lo -> voice+0x16/0x1d2/0x1d4/0x1d6."},
        // --- pitch envelope (confident) ---
        {"18005fde0","partial_compute_pitch_env","Pitch-env setup: depth(block+0x18), 5 stage biases(block+0x1b..+0x1f, each -0x40) -> per-voice pitch env targets."},
        // --- env-setup chain (provisional names, call-site + written-globals evidence) ---
        {"1800600c0","pitch_env_apply_stage","[provisional] Applies one pitch-env stage; called from partial_compute_pitch_env."},
        {"180060150","partial_apply_pitch_env_rates","[provisional] Applies pitch-env rate key-follow (g_kf_pitchrate0/1) after pitch-env compute."},
        {"180060620","tvf_env_prep","[provisional] TVF env preparation; called just before partial_compute_filter."},
        {"180060ca0","tva_compute_env_rates","[provisional] TVA env stage rates from rate key-follow (g_kf_tvarate0/1) + vel sens."},
    };
    // corrected plate comment for the filter compute (offsets re-verified)
    String[][] FC = {
        {"180061210","partial_compute_filter","TVF: cutoff base(block+0x2f, x0x100 -> voice+0x1f0), cutoff key-bias(+0x30), filter TYPE(+0x31: 0/1/2/4/5/6 else bypass -> g_filter_type_coef), env-depth key-follow(+0x32 nibble -> g_kf_tvfenv), env depth(+0x33, 0x40 center), 5 env levels(+0x3a..+0x3e via tvf_env_level_conv), env-rate KF(+0x46), resonance(+0x4a, 0x40 center -> g_reso_curve)."},
    };
    String[][] L = {
        {"1819a3060","g_env_rate_out"},     {"1819a28e8","g_env_scale_curve"},
        {"1819a2a00","g_level_curve"},      {"1819a2b00","g_vel_curve"},
        {"1819a2ba0","g_amp_curve_hi"},     {"1819a2da0","g_amp_curve_lo"},
        {"181987b00","g_filter_type_coef"}, {"1819a2b88","g_reso_curve"},
        {"181a01b20","g_kf_pitch"},         {"181a02a20","g_kf_tvfenv"},
        {"181a026a0","g_kf_tvalevel"},      {"181a01fa0","g_kf_tvalevel2"},
        {"181a023a0","g_kf_tvarate0"},      {"181a020a0","g_kf_tvarate1"},
        {"181a03920","g_kf_tvfrate"},       {"181a01f20","g_kf_pitchrate0"},
        {"181a01aa0","g_kf_pitchrate1"},
    };
    @Override public void run() throws Exception {
        int ok=0,lb=0;
        String[][] all = new String[F.length+FC.length][];
        System.arraycopy(F,0,all,0,F.length); System.arraycopy(FC,0,all,F.length,FC.length);
        for (String[] e : all){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); continue; }
            fn.setName(e[1],SourceType.USER_DEFINED); if(e.length>2&&!e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }
        println("RenameSynth: "+ok+" funcs, "+lb+" labels.");
    }
}
