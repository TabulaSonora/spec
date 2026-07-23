import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

public class RenameCodec extends GhidraScript {
    @Override public void run() throws Exception {
        rename(0x18003f4e0L, "dpcm_voice_init_fwd",
            "Block-FP DPCM sample-state init (forward). predictor += (int8)delta[i] << (scale+10);\n" +
            "out = predictor * 2^-27. delta stream +0x20 (1 byte/sample, 16-sample blocks), scale\n" +
            "stream +0x38 (nibble/block). Proven on 6 instruments via external decoder.");
        rename(0x18003ff90L, "dpcm_voice_init_rev",
            "REVERSE variant of dpcm_voice_init_fwd: identical DPCM accumulation but position\n" +
            "decrements, block index counts 0xf->0, blocks refill backward, scale byte read from\n" +
            "decreasing index. Selected when wave_ctrl bit 11 set (runflag 0x22/0x24), paired with\n" +
            "the _alt samplers (voice_render_dispatch & 0x20). Used by reverse SFX e.g. Reverse\n" +
            "Cymbal (GM prog 119, bank MSB 1). Confirmed by ear: decodes to a backwards cymbal.");
    }
    void rename(long addr, String name, String cmt) throws Exception {
        Address a = toAddr(addr); Function f = getFunctionAt(a);
        if (f==null){ println("MISS "+Long.toHexString(addr)); return; }
        f.setName(name, SourceType.USER_DEFINED); setPlateComment(a, cmt);
        println("named "+name);
    }
}
