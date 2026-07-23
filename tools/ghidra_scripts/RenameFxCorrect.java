import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Corrects the effects-engine naming in SCCore.dll after deeper analysis. */
public class RenameFxCorrect extends GhidraScript {

    private static final String[][] FUNCS = {
        {"18008c2c0", "fx_process_block"},   // REAL effects DSP engine (was unnamed); called by TG_Process
        {"180056560", "fx_program_load"},    // CORRECTION: prev mislabeled 'fx_process'. params->regs + preset load
        {"180055e90", "fx_param_apply"},     // CORRECTION: prev 'fx_param_update'. lighter param->reg delta
        {"1800898d0", "fx_reg_write"},       // reg write -> float coefficient (g_fx_coef_f32)
        {"1800621f0", "fx_reg_write_slew"},  // slewed reg write (ramps 1 step/call toward target)
        {"180062050", "fx_reg_write16"},     // 16-bit reg write across two registers
        {"180089830", "fx_delayline_wrap"},  // circular delay-line maintenance/rotate
    };

    private static final String[][] LABELS = {
        {"181a1af70", "g_fx_coef_f32"},      // effect coefficients as floats, indexed by register
        {"181a73cc0", "g_fx_reg_shadow"},    // shadow copy of written register bytes
        {"181a63460", "g_fx_algo_index"},    // current effect algorithm index (0..0x42)
        {"181895190", "g_fx_algo_dispatch"}, // PTR table: per-algorithm DSP processors, indexed by algo
    };

    @Override
    public void run() throws Exception {
        for (String[] e : FUNCS) {
            Address a = toAddr(Long.parseLong(e[0], 16));
            Function fn = getFunctionAt(a);
            if (fn == null) { println("MISS func " + e[0] + " -> " + e[1]); continue; }
            String old = fn.getName();
            fn.setName(e[1], SourceType.USER_DEFINED);
            println("func " + e[0] + "  " + old + " -> " + e[1]);
        }
        for (String[] e : LABELS) {
            createLabel(toAddr(Long.parseLong(e[0], 16)), e[1], true);
            println("label " + e[0] + " -> " + e[1]);
        }
        println("RenameFxCorrect done.");
    }
}
