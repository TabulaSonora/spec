import ghidra.app.script.GhidraScript;
import java.io.FileOutputStream;
/** Exports the envelope segment interpolation-shape curve (env_ramp_segment DAT_1819a7a90). */
public class ExportEnvShape extends GhidraScript {
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
    @Override public void run() throws Exception {
        // Label before exporting: a label is a program annotation, not a side effect of writing
        // files, and it should survive a failed write. 1819a7a30 is deliberately NOT labelled here
        // -- RenameDispatch.java owns it as g_env_startphase_b, and having two scripts name one
        // address made the result depend on which ran last.
        createLabel(toAddr(0x1819a7a90L), "g_env_shape", true);

        String d=tablesDir();
        dump(0x1819a7a90L, 0x204, d+"env_shape_7a90.bin");  // u16 interp curve (phase-hi -> fraction), +2 for the a92 pair
        dump(0x1819a7a30L, 0x100, d+"env_startphase_7a30.bin"); // u16, initial-phase table (env_ramp_segment param+0xe)
        println("ExportEnvShape done.");
    }
}
