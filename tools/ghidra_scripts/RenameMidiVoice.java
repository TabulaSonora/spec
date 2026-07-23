import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the MIDI->voice pipeline discovered by tracing from TG_ShortMidiIn. */
public class RenameMidiVoice extends GhidraScript {
    private static final String[][] FUNCS = {
        {"18008ab90", "midi_drain_ready_to_ports"}, // ready buffer -> midi_port_enqueue per event
        {"180080930", "midi_port_enqueue"},         // push event into per-port FIFO (DAT_181a22660)
        {"180061a40", "part_start_voices"},         // walk a part's partial list (+0x270), start voices
        {"18008f640", "voice_start"},               // fill per-voice SoA arrays from tone desc, then setup
        {"180089b60", "voice_setup_sample_playback"}, // voice idx<64: wave ROM ptr+loop from key
    };
    private static final String[][] LABELS = {
        {"181a1b5b8", "g_voice_run_flags"},   // per-voice active flags, stride 0x50, bit0=running
        {"181a6fb60", "g_voice_wave_ctrl"},   // per-voice wave/loop control word (bank/region/loop bits)
        {"181a18ef0", "g_wave_rom_base_a"},   // sample ROM base (bank A)
        {"181a11a68", "g_wave_rom_base_b"},   // sample ROM base (bank B, when wave_ctrl&0x10)
        {"181a0fb30", "g_midi_in_ring_count"},// pending events in the timestamped input ring
    };
    private static final String[][] COMMENTS = {
        {"180089370", // TG_ShortMidiIn
         "MIDI->voice pipeline (traced):\n" +
         " 1. TG_ShortMidiIn: decode status->class code, timestamp, enqueue to input ring (g_midi_in_ring_count).\n" +
         " 2. TG_Process: scheduler moves due events -> 'ready' buffer (TG_flushMidi does it immediately).\n" +
         " 3. midi_drain_ready_to_ports -> midi_port_enqueue -> per-port FIFO (DAT_181a22660).\n" +
         " 4. table-driven MIDI parser state machine (FUN_180072530 & friends) reassembles channel messages.\n" +
         " 5. part_start_voices: walk the part's active-partial list, allocate/start voices.\n" +
         " 6. voice_start: populate per-voice SoA param arrays (0x181a6f60..0x181a723xx) from tone descriptor.\n" +
         " 7. voice_setup_sample_playback: compute wave ROM pointer+loop from key; voice index 0..63 (64 voices).\n" +
         " 8. render_block samplers read the per-voice arrays -> audio (TG_Process output)."},
    };
    @Override
    public void run() throws Exception {
        for (String[] e : FUNCS) {
            Function fn = getFunctionAt(toAddr(Long.parseLong(e[0],16)));
            if (fn==null){ println("MISS "+e[0]); continue; }
            fn.setName(e[1], SourceType.USER_DEFINED);
        }
        for (String[] e : LABELS) createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true);
        for (String[] e : COMMENTS) setPlateComment(toAddr(Long.parseLong(e[0],16)), e[1]);
        println("RenameMidiVoice done.");
    }
}
