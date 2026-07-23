import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the 67 EFX algorithm processors + the type->index selection machinery. */
public class RenameAlgos extends GhidraScript {
    private static final String[][] FUNCS = {
{"18003d220", "fx_algo_thru"},
{"180018070", "fx_algo_none_placeholder"},
{"18001b730", "fx_algo_stereo_eq"},
{"18002daa0", "fx_algo_overdrive"},
{"180018560", "fx_algo_distortion"},
{"180034f30", "fx_algo_phaser"},
{"18003b150", "fx_algo_spectrum"},
{"18001b0e0", "fx_algo_enhancer"},
{"18000cbe0", "fx_algo_auto_wah"},
{"1800382f0", "fx_algo_rotary"},
{"180014b40", "fx_algo_compressor"},
{"1800259f0", "fx_algo_limiter"},
{"180022b80", "fx_algo_hexa_chorus"},
{"18003d440", "fx_algo_tremolo_chorus"},
{"18003a840", "fx_algo_space_d"},
{"18000f340", "fx_algo_stereo_chorus"},
{"18001c940", "fx_algo_stereo_flanger"},
{"18003ba70", "fx_algo_step_flanger"},
{"18003c510", "fx_algo_stereo_delay"},
{"18002a540", "fx_algo_modulation_delay"},
{"18003e2d0", "fx_algo_triple_tap_delay"},
{"180035b70", "fx_algo_quadruple_tap_delay"},
{"18003cba0", "fx_algo_time_controllable_delay"},
{"180005d30", "fx_algo_2_voice_pitch_shifter"},
{"18001de70", "fx_algo_feedback_pitch_shifter"},
{"180036230", "fx_algo_reverb"},
{"18001e7c0", "fx_algo_gate_reverb"},
{"18002af50", "fx_algo_overdrive_to_chorus"},
{"18002cb20", "fx_algo_overdrive_to_flanger"},
{"18002bed0", "fx_algo_overdrive_to_delay"},
{"180015520", "fx_algo_distortion_to_chorus"},
{"1800170f0", "fx_algo_distortion_to_flanger"},
{"1800164a0", "fx_algo_distortion_to_delay"},
{"180018f50", "fx_algo_enhancer_to_chorus"},
{"18001a450", "fx_algo_enhancer_to_flanger"},
{"180019be0", "fx_algo_enhancer_to_delay"},
{"18000e860", "fx_algo_chorus_to_delay"},
{"18001be60", "fx_algo_flanger_to_delay"},
{"180013d30", "fx_algo_chorus_to_flanger"},
{"18000fd90", "fx_algo_chorus_par_delay"},
{"18001d390", "fx_algo_flanger_par_delay"},
{"180010870", "fx_algo_chorus_par_flanger"},
{"18002f450", "fx_algo_overdrive_1_par_overdrive_2"},
{"1800312c0", "fx_algo_overdrive_par_rotary"},
{"180030460", "fx_algo_overdrive_par_phaser"},
{"18002e490", "fx_algo_overdrive_par_auto_wah"},
{"1800238f0", "fx_algo_humanizer"},
{"180026560", "fx_algo_lo_fi_1"},
{"180006c10", "fx_algo_3d_manual"},
{"18000c150", "fx_algo_3d_delay"},
{"18000b5d0", "fx_algo_3d_chorus"},
{"1800090f0", "fx_algo_3d_auto"},
{"18003e8a0", "fx_algo_tremolo"},
{"180032700", "fx_algo_auto_pan"},
{"18001f2c0", "fx_algo_guitar_multi_1"},
{"180021820", "fx_algo_guitar_multi_3"},
{"1800116a0", "fx_algo_c_guitar_multi_1"},
{"1800123c0", "fx_algo_c_guitar_multi_2"},
{"1800205c0", "fx_algo_guitar_multi_2"},
{"18000d5f0", "fx_algo_bass_multi"},
{"180036d70", "fx_algo_ep_multi"},
{"180024420", "fx_algo_keyboard_multi"},
{"1800390a0", "fx_algo_rotary_multi"},
{"180033d30", "fx_algo_phaser_par_rotary"},
{"180032ee0", "fx_algo_phaser_par_auto_wah"},
{"180027800", "fx_algo_lo_fi_2"},
{"180029c90", "fx_algo_internal_unused_66"},
        {"180062410", "fx_set_algo_index"},        // g_fx_algo_index = arg (1 => reset+0)
        {"18003f140", "fx_select_algo_from_type"},  // scans g_fx_type_to_algo_map, sets index
    };
    private static final String[][] LABELS = {
        {"18189566c", "g_fx_type_to_algo_map"},     // 66 recs x0x28: [+0 type key][+2 dispatch index]
        {"181a1dedc", "g_fx_current_type"},         // current EFX type value (MSB<<8|LSB)
    };
    @Override
    public void run() throws Exception {
        int ok=0, miss=0;
        for (String[] e : FUNCS) {
            if (e[0].equals("?")) { println("SKIP (no addr): "+e[1]); miss++; continue; }
            Address a = toAddr(Long.parseLong(e[0],16));
            Function fn = getFunctionAt(a);
            if (fn==null){ println("MISS "+e[0]+" -> "+e[1]); miss++; continue; }
            fn.setName(e[1], SourceType.USER_DEFINED); ok++;
        }
        for (String[] e : LABELS) { createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); }
        println("RenameAlgos: "+ok+" funcs renamed, "+miss+" missing, "+LABELS.length+" labels.");
    }
}
