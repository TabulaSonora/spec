import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/**
 * Applies confirmed names to the Sound Canvas VA render path in SCCore.dll.
 * Function renames + one data label for the interpolation coefficient table.
 */
public class RenameSauce extends GhidraScript {

    // {address, newName}
    private static final String[][] FUNCS = {
        {"18008b1d0", "render_block"},              // per-block engine: voices in groups of 4
        {"18003f720", "voice_render_dispatch"},     // picks sampler variant / anti-denormal fill
        {"18003f870", "sample_fetch_loop_wrap"},    // sample fetch + loop-point handling
        {"18003f9d0", "sampler_pcm"},               // fmt 0, mode A
        {"18003fb80", "sampler_adpcm4"},            // fmt 2, nibble-unpacked 4-bit samples
        {"18003fdd0", "sampler_fmt4"},              // fmt 4, mode A
        {"180040210", "sampler_pcm_alt"},           // fmt 0, mode B (0x20 flag)
        {"1800403c0", "sampler_adpcm4_alt"},        // fmt 2, mode B
        {"180040610", "sampler_fmt4_alt"},          // fmt 4, mode B
        {"18005e040", "voice_ctrl_ramp_a"},         // per-sample control-value ramp (stage 2)
        {"18005e990", "voice_ctrl_ramp_b"},         // per-sample control-value ramp (stage 2)
        {"18005d8d0", "voice_ctrl_ramp_c"},         // per-sample control-value ramp (stage 4)
        {"18005dbf0", "voice_ctrl_ramp_d"},         // per-sample control-value ramp (stage 4)
        {"180055e90", "fx_param_update"},           // effect param delta (prev vs current block)
        {"180056560", "fx_process"},                // vectorized effects DSP
    };

    // {address, labelName}
    private static final String[][] LABELS = {
        {"181a0f210", "g_interp_coef_table"},       // 128-phase x 4-tap FIR coefficients (16 bytes/entry)
    };

    @Override
    public void run() throws Exception {
        int renamed = 0, labeled = 0, missing = 0;

        for (String[] e : FUNCS) {
            Address a = toAddr(Long.parseLong(e[0], 16));
            Function fn = getFunctionAt(a);
            if (fn == null) {
                println("RenameSauce: NO FUNCTION at " + e[0] + " (wanted " + e[1] + ")");
                missing++;
                continue;
            }
            String old = fn.getName();
            fn.setName(e[1], SourceType.USER_DEFINED);
            println("RenameSauce: " + e[0] + "  " + old + " -> " + e[1]);
            renamed++;
        }

        for (String[] e : LABELS) {
            Address a = toAddr(Long.parseLong(e[0], 16));
            createLabel(a, e[1], true);
            println("RenameSauce: label " + e[0] + " -> " + e[1]);
            labeled++;
        }

        println("RenameSauce: " + renamed + " renamed, " + labeled + " labeled, " + missing + " missing.");
    }
}
