import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

/** Rename the orphaned algo 66 to reflect its confirmed DSP class + unreachable status. */
public class RenameOrphan extends GhidraScript {
    @Override
    public void run() throws Exception {
        Address a = toAddr(0x180029c90L);
        Function fn = getFunctionAt(a);
        if (fn == null) { println("MISS 180029c90"); return; }
        fn.setName("fx_algo_orphan66_moddelay", SourceType.USER_DEFINED);
        setPlateComment(a,
            "Dispatch slot 66 (0x42). Complete effect, standard algo ABI. UNREACHABLE: no\n" +
            "direct caller and g_fx_algo_index is never set to 66 (type-map scan caps at 65,\n" +
            "unmatched types fall to 0/Thru). DSP class: modulated multi-tap delay / chorus\n" +
            "(4 triangle LFOs via phase-wrap, delay taps 0x4000/0x8000/0xc000 + unique 0x5555).\n" +
            "The 0x5555 tap appears in no other algo, so it is not a duplicate. Likely a hidden/\n" +
            "reserved effect carried from the shared Roland DSP codebase but not exposed in SC-VA.");
        println("renamed + commented 180029c90");
    }
}
