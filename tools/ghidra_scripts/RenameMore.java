import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

public class RenameMore extends GhidraScript {
    // {addr, name, optional plate comment ("" = none)}
    String[][] F = {
        {"1800887c0","get_local_time_ms","GetLocalTime -> ms-since-midnight. Used to timestamp incoming MIDI (TG_ShortMidiIn)."},
        {"180090b74","alloc_retry","C++ operator-new style: malloc(size), retry via _callnewh on failure."},
        {"18008f020","tg_start_pending_voices","Per-block: for each part (stride 0x488) whose partial list (part+0x270) is non-empty, call part_start_voices."},
        {"18008aca0","tg_output_filter","Per-block output-stage biquad (state @ DAT_181a6e4a8, +0x30/0x34/0x38 filter memory). Called from TG_Process after fx_process_block. [likely: master output filter]"},
        {"18008a6c0","voice_pool_scan","Scans the voice pool (g_voice_run_flags @ 0x181a1b608) for allocation/priority. [likely]"},
        {"180089a10","voice_compute_mod_rates","Converts packed per-voice rate params (DAT_181a73560/735e0/73660/736e0) to float rates; called from voice_setup_sample_playback. [likely: LFO/env rate setup]"},
        {"18008b510","voice_set_ramp_target_0","Writes a per-voice control-ramp target (DAT_181a1cbf0, stride 0x18). One of 3 ramp-target setters from voice_setup_sample_playback. [likely]"},
        {"18008b660","voice_set_ramp_target_1","Writes a per-voice control-ramp target (DAT_181a10140). [likely]"},
        {"18008b790","voice_set_ramp_target_2","Writes a per-voice control-ramp target. [likely]"},
    };
    @Override public void run() throws Exception {
        int ok=0;
        for (String[] e : F){
            Address a = toAddr(Long.parseLong(e[0],16));
            Function fn = getFunctionAt(a);
            if (fn==null){ println("MISS "+e[0]); continue; }
            fn.setName(e[1], SourceType.USER_DEFINED);
            if (e.length>2 && !e[2].isEmpty()) setPlateComment(a, e[2]);
            ok++; println("named "+e[1]);
        }
        println("RenameMore: "+ok+" named.");
    }
}
