import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import java.io.FileOutputStream;

/** Export the static patch tables (tone/multisample/wave-desc, bank A) to raw binary files. */
public class ExportTables extends GhidraScript {
    void dump(long base, int total, String path) throws Exception {
        byte[] b = new byte[total];
        currentProgram.getMemory().getBytes(toAddr(base), b, 0, total);
        try (FileOutputStream f = new FileOutputStream(path)) { f.write(b); }
        println("wrote "+path+" ("+total+" bytes)");
    }
    @Override public void run() throws Exception {
        String d = "C:/Users/kevin/Projects/DeconstructingTheSauce/tables/";
        new java.io.File(d).mkdirs();
        dump(0x1818f2810L, 2048*0x100, d+"tone_a.bin");        // melodic tone table, stride 0x100
        dump(0x1818ca570L, 2048*0x8c,  d+"multisample_a.bin");  // multisample table, stride 0x8c
        dump(0x181897b40L, 4096*0x16,  d+"wavedesc_a.bin");     // wave-descriptor table, stride 0x16
    }
}
