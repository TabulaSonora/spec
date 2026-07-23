import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Decode the melodic tone table; find the tone whose partial references multisample #111 (flute). */
public class DecodeToneTable extends GhidraScript {
    int u(byte b){ return b&0xff; }
    int u16(byte[] d,int o){ return u(d[o])|(u(d[o+1])<<8); }
    @Override public void run() throws Exception {
        long base = 0x1818f2810L;  // g_tone_table_src_a, stride 0x100, 0x24 hdr + 2*0x6e partials
        int stride = 0x100, count = 800, targetMs = 111;
        byte[] t = new byte[stride];
        int found = 0;
        for (int i=0;i<count;i++){
            Address a=toAddr(base+(long)i*stride);
            try { currentProgram.getMemory().getBytes(a,t,0,stride); } catch(Exception e){ break; }
            for (int pi=0; pi<2; pi++){
                int off = 0x24 + pi*0x6e;
                int ms   = u16(t, off+2);
                if (ms != targetMs) continue;
                // this tone has a partial pointing at the flute multisample
                println(String.format("TONE #%d partial %d -> multisample %d:", i, pi, ms));
                for (int p2=0; p2<2; p2++){
                    int o=0x24+p2*0x6e;
                    println(String.format("   partial %d: msamp=%d keyCenter=%d keyTranspose=%d velLo=%d velHi=%d level=%d pkf_mode=0x%x",
                        p2, u16(t,o+2), u(t[o+4]), u(t[o+0x10]), u(t[o+0x4f]), u(t[o+0x51]), u(t[o+0x50]), u(t[o+0x13])));
                }
                // tone-common header (first 0x24 bytes): partial-enable mask @ +0x24? (agent: tone-common+0x24)
                println(String.format("   tone hdr[0..7]=%02x %02x %02x %02x %02x %02x %02x %02x",
                    u(t[0]),u(t[1]),u(t[2]),u(t[3]),u(t[4]),u(t[5]),u(t[6]),u(t[7])));
                if (++found>=4) return;
                break;
            }
        }
        if (found==0) println("no tone references multisample "+targetMs);
    }
}
