import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Names the envelope-generator + control-tick engine (rate->ms calibration trace). */
public class RenameEnvEngine extends GhidraScript {
    String[][] F = {
        {"180083a70","env_ramp_segment","Envelope segment ramp (16-bit phase accumulator @state+0xe): phase += rate(state+6) * (DAT_181a2283c + carry); on wrap past 0xffff segment completes (out=target), else out(state+0xc) interpolates start(+8)->target(+0xa) by phase. Segment ticks = 0x10000/(rate*speed)."},
        {"180080e40","voice_block_process","Per-voice per-control-tick: snapshot voice->scratch DAT_181a226d0, run env_ramp_segment + pitch/filter/level, write back. Handles voice-kill on env end (flag +0x188)."},
        {"1800849a0","voices_control_update","Control-tick update: iterates 64 voices (stride 0x220), calls voice_block_process on each active (state+0x168==1). Once per control tick."},
        {"18008f0d0","control_tick_dispatch","Runs voices_control_update while control-tick-due flag (DAT_181a745d8 bit15) set."},
    };
    String[][] L = {
        {"181a2283c","g_env_block_speed"},   // env advance speed (normally 1; sub-rate = g_env_block_speed2)
        {"181a2282c","g_env_block_speed2"},
        {"181a0f1a4","g_host_sample_rate"},  // set by TG_setSampleRate; internal render clamps to 32000
    };
    @Override public void run() throws Exception {
        int ok=0,lb=0;
        for (String[] e : F){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); continue; }
            fn.setName(e[1],SourceType.USER_DEFINED); if(e.length>2&&!e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }
        println("RenameEnvEngine: "+ok+" funcs, "+lb+" labels.");
    }
}
