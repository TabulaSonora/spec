import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** XG SysEx front end (2026-08).
 *
 *  The earlier naming passes labelled this whole subsystem as if it were GS. It is in fact a second,
 *  independent Yamaha XG parser, selected by XG System On and reachable only from the XG dispatch
 *  table `g_xg_dispatch_table` (1819a0870). Everything at 18007d5a0..18007eff0 is XG-exclusive --
 *  verified by call-graph: no non-XG caller reaches any of it.
 *
 *  Two functions previously called `gs_reset` / `gs_reset_execute` are XG System On. The real GS
 *  Reset (Roland 40 00 7F) is `sysex_system_common_reset` at 180071620.
 *
 *  Run AFTER the other Rename*.java passes so these names win.
 *  See docs/FINDINGS.md "XG is a second SysEx front end, and it re-maps the instrument". */
public class RenameXg extends GhidraScript {
    // Order matters where a rename frees a name for a later entry (gs_reset -> xg_system_on).
    String[][] F = {
        // --- shared bank/program path (GS + XG) ---
        {"180066860","part_bank_program_apply","apply pending bank MSB/LSB + program to all parts on the channel; branches on g_xg_mode"},

        // --- entry points / mode switching ---
        {"18006b380","xg_system_on_buffered","XG System On via the buffered path (addr 00 00 7E): reinit parts, map selector 0x77, install xg_sysex_dispatch"},
        {"18007e130","xg_system_on","XG System On via the streaming path; same reset, arms xg_sysex_dispatch"},
        {"18007d5a0","xg_sysex_dispatch","XG-mode byte dispatcher: 0x43 -> g_xg_dispatch_table; any Roland 0x41 leaves XG mode and reinstalls sysex_output_pump"},

        // --- XG address blocks ---
        {"18007d6c0","xg_system_param","XG System block 00 00 pp: master tune, volume, transpose, System On (7E), All Param Reset (7F)"},
        {"18007d830","xg_effect1_param","XG Effect1 block 02 01 pp: reverb type/time/return, chorus type/return"},
        {"18007d910","xg_multipart_param","XG Multi Part 08 nn pp; nn remapped via g_xg_part_remap. Writes out of bounds for nn >= 0x20 (only 32 parts allocated)"},
        {"18007dfa0","xg_drum_setup_param","XG Drum Setup 3n rr pp, XG parameter numbering; setup index = addrH & 0xf but only 8 setups allocated"},

        // --- XG System block leaves ---
        {"18007e010","xg_system_master_tune","XG System 00 00 00-03 Master Tune, 4 nibbles -> 12-bit"},
        {"18007e0f0","xg_system_transpose","XG System 00 00 06 Transpose, clamped"},
        {"18007e230","xg_all_param_reset","XG System 00 00 7F All Parameter Reset: clear controllers/sustain on every part"},

        // --- XG Effect1 leaves ---
        {"18007e2f0","xg_reverb_type_select","XG Reverb Type MSB/LSB -> internal macro via g_reverb_macro_table; unmatched types are dropped"},
        {"18007e410","xg_reverb_time_set","XG Reverb Time -> internal time, piecewise on the active reverb macro"},
        {"18007e4d0","xg_chorus_type_select","XG Chorus Type MSB/LSB -> internal macro via g_chorus_macro_table; unmatched types are dropped"},

        // --- XG Multi Part leaves, in parameter order ---
        {"18007e5d0","xg_part_bank_program","Multi Part 01/02/03 Bank MSB/LSB + Program; MSB >= 0x7e switches the part to drums"},
        {"18007e730","xg_part_rx_channel","Multi Part 04 Rcv Channel -> part+0x3d8; resets controllers, unlinks and reinserts the voice"},
        {"18007e7f0","xg_part_mono_poly","Multi Part 05 Mono/Poly -> part+0x3d9 bit7"},
        {"18007e830","xg_part_same_note_assign","Multi Part 06 Same Note Number Key On Assign -> part+0x3d9 bits 0-1"},
        {"18007e880","xg_part_mode","Multi Part 07 Part Mode: 0 normal, 1 drum, 3/4/5 drums1-3"},
        {"18007ea20","xg_part_note_shift","Multi Part 08 Note Shift -> part+0x3da, clamp 0x28..0x58"},
        {"18007ea60","xg_part_detune","Multi Part 09/0a Detune, 2 nibbles -> part+0x3db"},
        {"18007ecb0","xg_part_rx_switches","Multi Part 30-3f: the 16 Rcv switches -> part+0x3d6 bitmap"},
        {"18007ed10","xg_part_rx_bank_select","Multi Part 40 Rcv Bank Select -> part+0x3ec bit0"},
        {"18007edf0","xg_part_mod_param","Multi Part mod-control blocks: MW/Bend/CAT/PAT/AC1/AC2 x6 params, and scale tuning"},
        {"18007ef30","xg_part_portamento_switch","Multi Part 67 Portamento Switch (propagates across grouped parts)"},
        {"18007ef90","xg_part_portamento_time","Multi Part 68 Portamento Time -> part+0x463"},

        // --- the real GS reset, for contrast ---
        {"1800709c0","gs_system_common_dispatch","GS System Common 40 00 pp: master tune/volume/key-shift/pan, and 7F = GS Reset"},
        {"180071620","sysex_system_common_reset","GS Reset (40 00 7F 00): reset all parts, clear the SysEx TX buffer"},
    };
    String[][] L = {
        {"1819a0870","g_xg_dispatch_table"},
        {"1819a0190","g_xg_buffered_table"},
        {"1819a0990","g_xg_part_remap"},
        {"1819f31b0","g_drumkit_index"},
        {"181a00bb0","g_drum_bank_to_row"},
        {"181a225d8","g_xg_mode"},
        {"181a225bc","g_xg_addr_lo16"},
        {"181a1e297","g_xg_addr_hi"},
        {"181a225c8","g_xg_data_cursor"},
        {"181a225e0","g_xg_cur_part"},
    };
    @Override public void run() throws Exception {
        int ok=0, miss=0, lb=0;
        for (String[] e : F){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); miss++; continue; }
            fn.setName(e[1],SourceType.USER_DEFINED);
            if(e.length>2 && !e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ try{ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }catch(Exception ex){ println("lbl fail "+e[0]); } }
        println("RenameXg: "+ok+" funcs, "+miss+" missing, "+lb+" labels.");
    }
}
