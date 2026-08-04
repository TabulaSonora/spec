import ghidra.app.script.GhidraScript;
import java.io.FileOutputStream;
public class ExportLut extends GhidraScript {
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
        String d=tablesDir();
        dump(0x1819f2e30L, 0x80,   d+"lut1_2e30.bin");   // bank -> group (byte)
        dump(0x1819f28b0L, 0x4000, d+"lut2_28b0.bin");   // group*0x80 + mid -> group2 (byte)
        dump(0x1819f32b0L, 0x10000,d+"lut3_32b0.bin");   // group2*0x80 + prog -> tone# (s16)
    }
}
