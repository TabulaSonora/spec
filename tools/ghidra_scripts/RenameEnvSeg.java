import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;
import java.io.FileOutputStream;

/** Exports g_rate_curve (rate param -> segment ms) + relabels the TVA segment-rate machine. */
public class RenameEnvSeg extends GhidraScript {
    /** Table output dir: first script arg, else $SCVX_TABLES_DIR, else "tables/" relative to the
     *  working directory. Created if missing, so a fresh checkout needs no setup. */
    String tablesDir() {
        String[] a = getScriptArgs();
        String d = (a.length > 0 && !a[0].isEmpty()) ? a[0] : System.getenv("SCVX_TABLES_DIR");
        if (d == null || d.isEmpty()) d = "tables/";
        new java.io.File(d).mkdirs();
        return d.endsWith("/") ? d : d + "/";
    }
    @Override public void run() throws Exception {
        // The export is best-effort: this script's real job is the naming below, and a table dump
        // that cannot be written must not take the renames down with it.
        try {
            String d = tablesDir();
            byte[] b = new byte[0x100];
            currentProgram.getMemory().getBytes(toAddr(0x1819a2900L), b, 0, 0x100);
            try (FileOutputStream f = new FileOutputStream(d + "curve_segrate_2900.bin")) { f.write(b); }
            println("wrote curve_segrate_2900.bin 256");
        } catch (Exception e) { println("export skipped: " + e); }

        createLabel(toAddr(0x1819a2900L), "g_rate_curve", true);          // rate param -> segment ms (u16 x0x80)
        createLabel(toAddr(0x181a1f5b4L), "g_tva_rate_mult", true);        // env_rate_scale(kf0, block[0x67])
        createLabel(toAddr(0x181a1f5b6L), "g_tva_release_rate_mult", true);// env_rate_scale(kf1, block[0x68])
        createLabel(toAddr(0x181a1f5b0L), "g_tva_vel_mult_a", true);       // env_level_scale(vel, block[0x69])
        createLabel(toAddr(0x181a1f5b2L), "g_tva_vel_mult_b", true);       // env_level_scale(vel, block[0x6a])

        Function fn = getFunctionAt(toAddr(0x180060ca0L));
        if (fn != null) {
            fn.setName("tva_compute_env_rates", SourceType.USER_DEFINED);
            setPlateComment(toAddr(0x180060ca0L),
                "TVA segment rates. 4 main segs rate=block[0x5e..0x61], release=block[0x62]. Per seg: "
              + "idx=clamp(rate&0x7f + partBias); T=g_rate_curve[idx]; "
              + "uVar6=(vel_mult*min(0xffff,(rate_mult*T)>>8))>>8; stored_rate=0xa0000/uVar6 -> voice+0x12/0x1c6/0x1c8/0x1ca/0x26. "
              + "segment_time_ms == uVar6 (== T at neutral mults). segs0/1 use vel_mult_a, 2/3 & release use vel_mult_b; "
              + "release uses release_rate_mult. Levels via tva_compute_env_levels (block[0x5a..0x5d]).");
        } else println("MISS tva_compute_env_rates");
        println("RenameEnvSeg done.");
    }
}
