import ghidra.app.script.GhidraScript;
import java.io.FileOutputStream;
/** Exports the envelope segment interpolation-shape curve (env_ramp_segment DAT_1819a7a90). */
public class ExportEnvShape extends GhidraScript {
    void dump(long base,int n,String p) throws Exception {
        byte[] b=new byte[n]; currentProgram.getMemory().getBytes(toAddr(base),b,0,n);
        try(FileOutputStream f=new FileOutputStream(p)){f.write(b);} println("wrote "+p+" "+n);
    }
    @Override public void run() throws Exception {
        String d="tables/";
        dump(0x1819a7a90L, 0x204, d+"env_shape_7a90.bin");  // u16 interp curve (phase-hi -> fraction), +2 for the a92 pair
        dump(0x1819a7a30L, 0x100, d+"env_startphase_7a30.bin"); // u16, initial-phase table (env_ramp_segment param+0xe)
        createLabel(toAddr(0x1819a7a90L), "g_env_shape", true);
        createLabel(toAddr(0x1819a7a30L), "g_env_startphase", true);
        println("ExportEnvShape done.");
    }
}
