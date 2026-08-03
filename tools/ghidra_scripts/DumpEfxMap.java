import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/**
 * Dumps the EFX type->algorithm directory: 66 records of 0x28 bytes.
 *
 * The symbol g_fx_type_to_algo_map lands on the type key, 12 bytes into the record, not on the
 * record start. Dumping from the symbol reads each effect's name against the *previous* effect's
 * type key, which is why this map once looked like a scramble of bare numbers. Start 12 bytes
 * earlier and every effect names itself:
 *
 *   +0x00 char name[12]   +0x0C u16 type key   +0x0E u16 dispatch index
 *   +0x10 param_apply     +0x18 param_defaults +0x20 common (same in all 66)
 *
 * Record 66 is not a record -- it reads as noise, which is what pins the count at 66.
 */
public class DumpEfxMap extends GhidraScript {

    /** Record start. The g_fx_type_to_algo_map symbol is this + 0xC. */
    private static final long BASE = 0x181895660L;
    private static final int COUNT = 66;
    private static final int STRIDE = 0x28;

    @Override
    public void run() throws Exception {
        println("idx  name          type   disp  param_apply  param_defaults");
        for (int i = 0; i < COUNT; i++) {
            Address rec = toAddr(BASE + (long) i * STRIDE);

            StringBuilder name = new StringBuilder();
            for (int j = 0; j < 12; j++) {
                name.append((char) (getByte(rec.add(j)) & 0xff));
            }

            int key = getShort(rec.add(0x0C)) & 0xffff;
            int disp = getShort(rec.add(0x0E)) & 0xffff;
            long apply = getLong(rec.add(0x10));
            long defaults = getLong(rec.add(0x18));

            // The 0xFFFF record is "no effect assigned": blank name, null apply handler.
            String type = key == 0xFFFF ? "--   " : String.format("%02X %02X", key >> 8, key & 0xff);

            println(String.format("%2d  %-12s  %s  %3d   %09x    %09x",
                    i, name.toString().trim(), type, disp, apply, defaults));
        }
    }
}
