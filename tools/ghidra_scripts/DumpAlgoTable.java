import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

/** Dumps the effect-algorithm dispatch table at g_fx_algo_dispatch (0x181895190), 67 entries. */
public class DumpAlgoTable extends GhidraScript {
    @Override
    public void run() throws Exception {
        long base = 0x181895190L;
        int count = 0x43; // 67
        java.util.LinkedHashMap<Long,int[]> uniq = new java.util.LinkedHashMap<>();
        java.util.ArrayList<String> rows = new java.util.ArrayList<>();

        for (int i = 0; i < count; i++) {
            Address slot = toAddr(base + (long) i * 8);
            long ptr = getLong(slot);
            Address fa = toAddr(ptr);
            Function fn = getFunctionContaining(fa);
            String name = (fn != null) ? fn.getName() : "<none>";
            long size = (fn != null) ? fn.getBody().getNumAddresses() : -1;
            long entry = (fn != null) ? fn.getEntryPoint().getOffset() : ptr;
            rows.add(String.format("algo %2d (0x%02x): %08x  %-22s  size=%d", i, i, entry, name, size));
            int[] agg = uniq.get(entry);
            if (agg == null) { uniq.put(entry, new int[]{1, (int) size}); }
            else { agg[0]++; }
        }

        println("=== ALGO TABLE (0x181895190, " + count + " entries) ===");
        for (String r : rows) println(r);

        println("=== UNIQUE TARGET FUNCTIONS (" + uniq.size() + ") ===");
        for (java.util.Map.Entry<Long,int[]> e : uniq.entrySet()) {
            println(String.format("%08x  refs=%d  size=%d", e.getKey(), e.getValue()[0], e.getValue()[1]));
        }
    }
}
