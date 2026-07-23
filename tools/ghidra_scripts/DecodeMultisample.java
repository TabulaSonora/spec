import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Find the multisample referencing flute wave #806 and decode its key-zone map. */
public class DecodeMultisample extends GhidraScript {
    int u(byte b){ return b&0xff; }
    short s16(byte[] d,int o){ return (short)((u(d[o]))|(u(d[o+1])<<8)); }
    @Override public void run() throws Exception {
        long base = 0x1818ca570L;  // g_multisample_src_a, stride 0x8c
        int stride = 0x8c, count = 1200, target = 806;
        byte[] d = new byte[stride];
        for (int i=0;i<count;i++){
            Address a=toAddr(base+(long)i*stride);
            try { currentProgram.getMemory().getBytes(a,d,0,stride); } catch(Exception e){ break; }
            boolean has=false;
            for (int z=0; z<0x20; z++){ if (s16(d,0x2c+z*2)==target){ has=true; break; } }
            if (!has) continue;
            println("MULTISAMPLE #"+i+" references wave #"+target+":");
            // key-split bounds at +0x0c (bytes, terminated 0x7f); wave# at +0x2c (s16 array)
            StringBuilder sb=new StringBuilder("  key bounds: ");
            int prev=0;
            for (int z=0; z<0x20; z++){
                int bound=u(d[0x0c+z]);
                short w=s16(d,0x2c+z*2);
                short wlo=s16(d,0x2a+z*2), whi=s16(d,0x2e+z*2);
                if (w>=0 || wlo>=0 || whi>=0)
                    println(String.format("    keys %d..%d -> wave# %d (velLo-alt %d, velHi-alt %d)", prev, bound, w, wlo, whi));
                prev=bound+1;
                if (bound>=0x7f) break;
            }
            println("  fallback wave# @+0x6a = "+s16(d,0x6a));
            return;
        }
        println("no multisample references wave #"+target);
    }
}
