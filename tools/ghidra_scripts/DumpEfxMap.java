import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Dumps the EFX type->dispatch-index table at 0x18189566c (66 entries, 0x28 stride). */
public class DumpEfxMap extends GhidraScript {
    @Override
    public void run() throws Exception {
        long base = 0x18189566cL;
        int n = 0x42; // 66
        int stride = 0x28;
        println("idx_in_table  key(hex)  dispatch_index  [next few shorts]");
        for (int i = 0; i < n; i++) {
            Address rec = toAddr(base + (long) i * stride);
            int key = getShort(rec) & 0xffff;
            int disp = getShort(rec.add(2)) & 0xffff;
            StringBuilder extra = new StringBuilder();
            for (int j = 2; j < 8; j++) extra.append(String.format(" %04x", getShort(rec.add(j * 2)) & 0xffff));
            println(String.format("%2d  key=%04x  disp=%2d (0x%02x) |%s", i, key, disp, disp, extra));
        }
    }
}
