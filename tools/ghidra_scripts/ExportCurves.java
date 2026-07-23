import ghidra.app.script.GhidraScript;
import java.io.FileOutputStream;

// Export the per-partial synthesis curve/param tables (the "back half").
public class ExportCurves extends GhidraScript {
    void dump(long base, int n, String p) throws Exception {
        byte[] b = new byte[n];
        currentProgram.getMemory().getBytes(toAddr(base), b, 0, n);
        try (FileOutputStream f = new FileOutputStream(p)) { f.write(b); }
        println("wrote " + p + " " + n);
    }
    @Override public void run() throws Exception {
        String d = "C:/Users/kevin/Projects/DeconstructingTheSauce/tables/";
        // --- envelope RATE math (FUN_1800607e0 / FUN_180060880) ---
        dump(0x1819a3060L, 0x400,  d+"curve_rateout_3060.bin");   // u16 rate multiplier output (8.8)
        dump(0x1819a28e8L, 0x100,  d+"curve_scale_28e8.bin");     // byte scaling curve
        // --- TVA level math (FUN_180060960 / FUN_180060b00) ---
        dump(0x1819a2a00L, 0x100,  d+"curve_level_2a00.bin");     // u16 level->attenuation
        dump(0x1819a2b00L, 0x100,  d+"curve_vel_2b00.bin");       // byte velocity curve
        dump(0x1819a2ba0L, 0x200,  d+"curve_amp_hi_2ba0.bin");    // u16 amplitude coarse
        dump(0x1819a2da0L, 0x200,  d+"curve_amp_lo_2da0.bin");    // u16 amplitude fine
        dump(0x1819a2578L, 0x358,  d+"curve_pitchenv_2578.bin");  // u16 (2578 & 28d0 & 2890 region)
        // --- TVF filter (partial_compute_filter) ---
        dump(0x181987b00L, 0x40,   d+"curve_filttype_987b00.bin"); // 4B/entry filter coeff sets (types 0..6)
        dump(0x1819a2b88L, 0x80,   d+"curve_reso_2b88.bin");      // s16 resonance
        dump(0x1819a2f00L, 0x160,  d+"curve_filtenvdepth_2f00.bin"); // u16 env depth (covers 2fa8 & 3028)
        // --- 2D key-follow tables ([selector*0x80 + key]) ---
        dump(0x181a01b20L, 0x800,  d+"kf_pitch_01b20.bin");       // s16 pitch key-follow (8 rows)
        dump(0x181a02a20L, 0x8000, d+"kf_tvfenv_02a20.bin");      // u16 TVF env-depth key-follow (nibble rows)
        dump(0x181a026a0L, 0x4000, d+"kf_tvalevel_026a0.bin");    // char TVA level key-follow
        dump(0x181a01fa0L, 0x4000, d+"kf_tvalevel2_01fa0.bin");   // char TVA level key-follow 2
        dump(0x181a023a0L, 0x4000, d+"kf_tvarate0_023a0.bin");    // byte TVA rate key-follow 0
        dump(0x181a020a0L, 0x4000, d+"kf_tvarate1_020a0.bin");    // byte TVA rate key-follow 1
        dump(0x181a03920L, 0x4000, d+"kf_tvfrate_03920.bin");     // byte TVF env-rate key-follow
        dump(0x181a01f20L, 0x4000, d+"kf_pitchrate0_01f20.bin");  // byte pitch-env rate key-follow 0
        dump(0x181a01aa0L, 0x4000, d+"kf_pitchrate1_01aa0.bin");  // byte pitch-env rate key-follow 1
    }
}
