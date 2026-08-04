import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import java.io.FileOutputStream;

/** Names the LFO/vibrato engine + PRNG, exports the LFO tables. */
public class RenameLfo extends GhidraScript {
    /** Table output dir: first script arg, else $SCVX_TABLES_DIR, else "tables/" relative to the
     *  working directory. Created if missing, so a fresh checkout needs no setup. */
    String tablesDir() {
        String[] a = getScriptArgs();
        String d = (a.length > 0 && !a[0].isEmpty()) ? a[0] : System.getenv("SCVX_TABLES_DIR");
        if (d == null || d.isEmpty()) d = "tables/";
        new java.io.File(d).mkdirs();
        return d.endsWith("/") ? d : d + "/";
    }
    void dump(long base,int n,String p) throws Exception {
        byte[] b=new byte[n]; currentProgram.getMemory().getBytes(toAddr(base),b,0,n);
        try(FileOutputStream f=new FileOutputStream(p)){f.write(b);} println("wrote "+p+" "+n);
    }
    String[][] F = {
        {"18008fbb0","prng_lfsr","Galois LFSR pseudo-random source (DAT_181a6f630/634). Used for LFO random/S&H waveforms. (Was mislabeled lfo_value.)"},
        {"180081b90","lfo_update","Per-control-tick LFO/vibrato update (one of 128). Delay/fade phase accumulators (DAT_181a2280c/0e += rate*speed); rate = g_lfo_rate_tbl[vibRate]; computes pitch/TVF/TVA mod depths from part vib params (part+0x3a8..0x3ae) + mod-wheel; writes per-voice mod array DAT_181a227d8[v*6] {pitch,TVF,TVA}."},
        {"180082a30","lfo_advance_waveform","Advances LFO phase (DAT_181a2280a += rate) and evaluates waveform DAT_181a227d0: 0=sine/tri table g_lfo_wave_tbl, 1=random S&H (prng_lfsr), 2/3=slewed random(+-0x50), 4=square, 5=saw, 6=triangle. Output -> DAT_181a22808."},
        {"180082990","lfo_pitch_depth_cents","Vibrato pitch depth -> cents via g_lfo_cents_tbl (index 0..0x7f, clamp +-6000 cents = +-6 semitones)."},
        {"1800820f0","lfo_apply_depth","Scales LFO output by depth (DAT_181a2280e fade) and dispatches to waveform via jump table g_lfo_wavefn[0..2] (pitch/TVA/TVF)."},
    };
    String[][] L = {
        {"1819a1740","g_lfo_wave_tbl"}, {"1819a2790","g_lfo_rate_tbl"}, {"1819a2690","g_lfo_cents_tbl"},
        {"1819a10a8","g_lfo_wavefn"}, {"181a22808","g_lfo_out"}, {"181a2280a","g_lfo_phase"},
        {"181a227d0","g_lfo_waveform_sel"},
    };
    @Override public void run() throws Exception {
        // Best-effort: this script's real job is the naming below, and a table dump that cannot be
        // written must not take the renames down with it.
        try {
            String d=tablesDir();
            dump(0x1819a1740L, 0x100, d+"lfo_wave_1740.bin");   // waveform table (byte)
            dump(0x1819a2790L, 0x100, d+"lfo_rate_2790.bin");   // vibRate -> phase increment (u16 x0x80)
            dump(0x1819a2690L, 0x100, d+"lfo_cents_2690.bin");  // pitch depth -> cents (s16 x0x80)
        } catch (Exception e) { println("export skipped: " + e); }
        int ok=0,lb=0;
        for (String[] e : F){ Address a=toAddr(Long.parseLong(e[0],16)); Function fn=getFunctionAt(a);
            if(fn==null){ println("MISS fn "+e[0]); continue; }
            fn.setName(e[1],SourceType.USER_DEFINED); if(e.length>2&&!e[2].isEmpty()) setPlateComment(a,e[2]); ok++; }
        for (String[] e : L){ createLabel(toAddr(Long.parseLong(e[0],16)), e[1], true); lb++; }
        println("RenameLfo: "+ok+" funcs, "+lb+" labels.");
    }
}
