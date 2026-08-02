import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/**
 * Names the table-dispatched functions Ghidra only reaches once DefineTableFunctions has run —
 * the envelope stage loaders and the per-controller MIDI handlers — plus the tables themselves.
 *
 * <p>Every name here is from behaviour read out of the function body; anything still unidentified
 * is deliberately left as FUN_.
 */
public class RenameDispatch extends GhidraScript {

    /** Functions: address, name, plate comment. */
    String[][] F = {
        // --- pitch envelope stage loaders (dispatch table g_pitch_env_stage_handlers) ---
        {"180083870", "pitch_env_stage1_load", "Pitch-env stage 1: start<-current target, target<-voice+0x210, rate word from the stored ms at voice+0x204 (T<11 ? 0xffff : 0xa0000/T), shape 0x4000, interp <- g_env_startphase[min(T,10)]."},
        {"180083800", "pitch_env_stage2_load", "Pitch-env stage 2: target<-voice+0x214, time voice+0x206. Same form as stage 1."},
        {"180083790", "pitch_env_stage3_load", "Pitch-env stage 3: target<-voice+0x218 (the unbiased base pitch), time voice+0x208. Stage 4 is terminal and dispatches to the no-op."},
        // --- TVA envelope stage loaders (dispatch table g_tva_env_stage_handlers) ---
        {"1800838e0", "tva_env_stage1_load", "TVA-env stage 1: target<-voice+0x1d2, time voice+0x1c6, interp table g_env_startphase_b. Same machine as the pitch loaders."},
        {"180083960", "tva_env_stage2_load", "TVA-env stage 2: target<-voice+0x1d4, time voice+0x1c8."},
        {"1800839e0", "tva_env_stage3_load", "TVA-env stage 3: target<-voice+0x1d6, time voice+0x1ca."},
        // --- MIDI controller handlers (dispatch table g_cc_handlers, index = controller number) ---
        {"180065e50", "cc64_hold_damper", "CC64 damper. Rx gate 0x820 in part+0x3d6. Stores the RAW value into part+0x462 when the part's tone carries half-damper (part+0x24c bit 2, from tone header byte 0x0d); otherwise quantises to 0 / 0x7f. At release the value scales the release ramp rate: rate*(0xffff-(v<<9))>>16."},
        {"1800661a0", "cc66_sostenuto", "CC66 sostenuto, binary (bit 6 only). Rx gate 0x880. Down: for each sounding note-group (node+0x30==1) set the note's bit in the captured-note bitmap part+0x260 and the capture flag node+0x34 bit 0. Up: clear the flags, mark voices released (voice+0x16d=1) for groups already in state 2, then zero the bitmap."},
        {"180065c70", "cc67_soft_pedal", "CC67 soft pedal, binary. Rx gate 0x900. Sets/clears bit 3 of part+0x08."},
        {"180065eb0", "cc11_expression", "CC11 expression -> part+0x464. Rx gate 0x810."},
    };

    /** Data labels: address, name. */
    String[][] L = {
        {"1819a17c8", "g_pitch_env_stage_handlers"},
        {"1819a2408", "g_tva_env_stage_handlers"},
        {"1819a3054", "g_env_next_stage"},
        {"18199fb30", "g_cc_handlers"},
        {"1819a7a00", "g_env_startphase_pitch"},
        {"1819a7a30", "g_env_startphase_b"},
    };

    @Override
    public void run() throws Exception {
        int functions = 0;
        for (String[] e : F) {
            Address a = toAddr(Long.parseLong(e[0], 16));
            Function fn = getFunctionAt(a);
            if (fn == null) {
                println("RenameDispatch: no function at " + e[0] + " (run DefineTableFunctions first)");
                continue;
            }

            fn.setName(e[1], SourceType.USER_DEFINED);
            if (e.length > 2 && !e[2].isEmpty()) {
                setPlateComment(a, e[2]);
            }

            functions++;
        }

        int labels = 0;
        for (String[] e : L) {
            Address a = toAddr(Long.parseLong(e[0], 16));
            createLabel(a, e[1], true, SourceType.USER_DEFINED);
            labels++;
        }

        println("RenameDispatch: " + functions + " functions, " + labels + " labels");
    }
}
