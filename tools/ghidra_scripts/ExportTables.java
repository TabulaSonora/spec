import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.io.FileOutputStream;

/**
 * Export the static patch tables (tone/multisample/wave-desc, bank A) to raw binary files.
 *
 * The record counts below are MEASURED, not chosen. Two of them started as round powers of two and
 * both were short, which is invisible until something indexes past the end and renders silence:
 *
 *   tone       2048 -> 2363   drum kits reference tone 2353; records read as tone records (name,
 *                             level, the 01 00 00 at +0x0d) through 2362 and 2363 does not; the next
 *                             object, g_ramp_exp_tbl, is at 0x181986420.
 *   wavedesc   4096 -> 4259   multisamples a defined tone reaches name wave 4258; 4259 records end
 *                             at 0x18189ad942, fourteen bytes below g_drum_kits at 0x18189ad950.
 *   multisample     2048      genuinely has room to spare: nothing reaches past 1174.
 *
 * Nothing in the engine bounds any of these -- tone_lookup @1800026d0 tests only tone# < 0x4000 and
 * indexes -- so the lengths are layout facts and have to be pinned from the layout, not guessed.
 * See FINDINGS, "The tone table is 2363 records" and "The wave descriptor table is 4259 records".
 */
public class ExportTables extends GhidraScript {
    /** Table output dir: first script arg, else $SCVX_TABLES_DIR, else "tables/" relative to the
     *  working directory. Created if missing, so a fresh checkout needs no setup. */
    String tablesDir() {
        String[] a = getScriptArgs();
        String d = (a.length > 0 && !a[0].isEmpty()) ? a[0] : System.getenv("SCVX_TABLES_DIR");
        if (d == null || d.isEmpty()) d = "tables/";
        new java.io.File(d).mkdirs();
        return d.endsWith("/") ? d : d + "/";
    }
    static final int TONE_RECORDS = 2363;
    static final int MULTISAMPLE_RECORDS = 2048;
    static final int WAVEDESC_RECORDS = 4259;

    void dump(long base, int total, String path) throws Exception {
        byte[] b = new byte[total];
        currentProgram.getMemory().getBytes(toAddr(base), b, 0, total);
        try (FileOutputStream f = new FileOutputStream(path)) { f.write(b); }
        println("wrote "+path+" ("+total+" bytes)");
    }
    @Override public void run() throws Exception {
        String d = tablesDir();
        dump(0x1818f2810L, TONE_RECORDS*0x100,        d+"tone_a.bin");         // stride 0x100
        dump(0x1818ca570L, MULTISAMPLE_RECORDS*0x8c,  d+"multisample_a.bin");  // stride 0x8c
        dump(0x181897b40L, WAVEDESC_RECORDS*0x16,     d+"wavedesc_a.bin");     // stride 0x16
    }
}
