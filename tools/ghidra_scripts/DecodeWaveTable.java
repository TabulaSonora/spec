import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Decode the wave-descriptor table (0x16 stride) at its .rdata source, validate vs the flute. */
public class DecodeWaveTable extends GhidraScript {
    int u(byte b){ return b & 0xff; }
    @Override public void run() throws Exception {
        long[] cands = {0x181897b40L,0x1818ca570L,0x1818f2810L,0x1818ae950L,0x181897690L,0x180093700L};
        for (long cb : cands){ if (tryBase(cb)) return; }
        println("flute not found at any candidate base");
    }
    boolean tryBase(long base) throws Exception {
        int stride = 0x16, count = 2300;
        byte[] d = new byte[stride];
        int fluteIdx = -1;
        for (int i = 0; i < count; i++){
            Address a = toAddr(base + (long)i*stride);
            try { currentProgram.getMemory().getBytes(a, d, 0, stride); } catch(Exception e){ break; }
            int region = u(d[0]) & 0x7f;
            int loop  = ((u(d[1])&0xf)<<16) | (u(d[2])<<8) | u(d[3]);
            int end   = ((u(d[7])&0xf)<<16) | (u(d[8])<<8) | u(d[9]);
            int start = ((u(d[0xb])&0xf)<<16) | (u(d[0xc])<<8) | u(d[0xd]);
            int root  = u(d[6]);
            int fine  = u(d[4]) | (u(d[5])<<8);
            int flags = u(d[0xa]);
            if (region==6 && loop==800928 && start==807803){
                fluteIdx = i;
                println(String.format("FLUTE FOUND at base 0x%x wave#%d: region=%d loop=%d end=%d start=%d root=%d fine=%d flags=0x%x",
                    base, i, region, loop, end, start, root, fine, flags));
            }
        }
        if (fluteIdx<0){ return false; }
        // dump a few descriptors around the flute for structure sanity
        println("=== descriptors around flute (idx, region, loop, end, start, root, flags) ===");
        for (int i = Math.max(0,fluteIdx-2); i <= fluteIdx+3; i++){
            Address a = toAddr(base + (long)i*stride);
            currentProgram.getMemory().getBytes(a, d, 0, stride);
            int region = u(d[0]) & 0x7f;
            int loop  = ((u(d[1])&0xf)<<16) | (u(d[2])<<8) | u(d[3]);
            int end   = ((u(d[7])&0xf)<<16) | (u(d[8])<<8) | u(d[9]);
            int start = ((u(d[0xb])&0xf)<<16) | (u(d[0xc])<<8) | u(d[0xd]);
            println(String.format("  #%d region=%d loop=%d end=%d start=%d root=%d flags=0x%x len=%d",
                i, region, loop, end, start, u(d[6]), u(d[0xa]), start-loop));
        }
        return true;
    }
}
