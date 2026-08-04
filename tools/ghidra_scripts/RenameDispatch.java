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
        // --- the shared do-nothing dispatch target ---
        {"1800052d0", "dispatch_noop", "The engine's shared no-op handler: a bare `ret 0` (c2 00 00). 175 of the 176 pointers that resolve here are engine dispatch tables -- every unimplemented slot of g_cc_handlers, slot 0 of all three envelope stage tables, the pitch table's terminal slot, and fn-ptr globals initialised at startup. Three identities are folded onto this address by /OPT:ICF, all of them a bare ret: the engine no-op above; the export TG_setInterruptThreadIdAtThisTime (ordinal 14), a stub -- every other export sits together at 0x88xxx-0x8axxx, so one landing alone in engine code is what folding leaves behind; and the CRT's inert __guard_check_icall_fptr default, which is the pointer at 1800921b8 and the reason Ghidra called this _guard_check_icall. That last name is misleading rather than baseless: Control Flow Guard is NOT enabled here -- the whole guard region of the load config directory (offsets 0xa0-0xff, covering GuardCFCheckFunctionPointer, GuardCFDispatchFunctionPointer and GuardFlags) is zero -- so nothing guards any indirect call and the fptr pair is vestigial CRT data. dispatch_noop is the identity that matters when reading the dispatch tables."},
        // --- pitch envelope stage loaders (dispatch table g_pitch_env_stage_handlers) ---
        {"180083870", "pitch_env_stage1_load", "Pitch-env stage 1: start<-current target, target<-voice+0x210, rate word from the stored ms at voice+0x204 (T<11 ? 0xffff : 0xa0000/T), shape 0x4000, interp <- g_env_startphase[min(T,10)]."},
        {"180083800", "pitch_env_stage2_load", "Pitch-env stage 2: target<-voice+0x214, time voice+0x206. Same form as stage 1."},
        {"180083790", "pitch_env_stage3_load", "Pitch-env stage 3: target<-voice+0x218 (the unbiased base pitch), time voice+0x208. Stage 4 is terminal: unlike TVA and TVF, the pitch table's stage-4 slot holds the same bare-return stub as slot 0, so the pitch envelope simply stops."},
        // --- TVA envelope stage loaders (dispatch table g_tva_env_stage_handlers) ---
        {"1800838e0", "tva_env_stage1_load", "TVA-env stage 1: target<-voice+0x1d2, time voice+0x1c6, interp table g_env_startphase_b. Same machine as the pitch loaders."},
        {"180083960", "tva_env_stage2_load", "TVA-env stage 2: target<-voice+0x1d4, time voice+0x1c8."},
        {"1800839e0", "tva_env_stage3_load", "TVA-env stage 3: target<-voice+0x1d6, time voice+0x1ca."},
        {"180083a60", "tva_env_stage4_hold", "TVA-env stage 4, the terminal state (g_env_next_stage maps 3->4 and 4->4). Loads no segment: it pins the interp start phase at voice+0xe to 0x33, which is g_env_startphase_b[10] -- the slowest entry, the one the loaders clamp to for any time >= 11 ms. The ramp therefore stops advancing rather than being switched off."},
        // --- TVF envelope stage loaders (dispatch table g_tvf_env_stage_handlers) ---
        {"1800846f0", "tvf_env_stage1_load", "TVF-env stage 1: target<-voice+0x1e4, time voice+0x1d8, shape voice+0x1de, interp table g_tvf_env_startphase. Same machine as the TVA loaders; the TVF block sits 0x12 above the TVA one (0x1c6/0x1cc/0x1d2 -> 0x1d8/0x1de/0x1e4). tvf_compute_env_rates writes the fields these read."},
        {"180084770", "tvf_env_stage2_load", "TVF-env stage 2: target<-voice+0x1e6, time voice+0x1da, shape voice+0x1e0."},
        {"1800847f0", "tvf_env_stage3_load", "TVF-env stage 3: target<-voice+0x1e8, time voice+0x1dc, shape voice+0x1e2."},
        {"180084870", "tvf_env_stage4_hold", "TVF-env stage 4, the terminal state. Same shape as tva_env_stage4_hold: pins the interp start phase (the DAT_181a226fa scratch) to 0xcc, which is g_tvf_env_startphase[10]. Takes no argument -- the TVF env runs out of scratch globals, not the voice."},
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
        {"1819a2430", "g_tvf_env_stage_handlers"},
        {"1819a3054", "g_env_next_stage"},
        {"18199fb30", "g_cc_handlers"},
        // The CRT's guard fptr pair, left inert by a non-CFG build: the check pointer targets
        // dispatch_noop and the dispatch pointer a bare `jmp rax`. Nothing in the load config
        // registers them, so no indirect call is actually guarded.
        {"1800921b8", "g_guard_check_icall_fptr_unused"},
        {"1800921c0", "g_guard_dispatch_icall_fptr_unused"},
        // 1819a7a00 is deliberately absent: RenameBulk2607.java owns it as g_tvf_env_startphase.
        // Neither that name nor g_env_startphase_pitch is quite right -- the table is read by the
        // pitch-env path AND the TVF-env path -- so naming it in two scripts only made the result
        // depend on run order and disagree with SYMBOLS.md. Keeping the documented name.
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
