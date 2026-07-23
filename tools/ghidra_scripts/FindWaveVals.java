import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.Memory;

/** Locate individual flute wave values (raw + region-combined) anywhere in the image. */
public class FindWaveVals extends GhidraScript {
    @Override public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        long region = 6L<<20;
        // name, value
        Object[][] targets = {
            {"loop_raw", 800928L}, {"end_raw", 803508L}, {"start_raw", 807803L},
            {"loop|region", region|800928L}, {"end|region", region|803508L}, {"start|region", region|807803L},
            {"loop>>5", 800928L>>5}, {"start>>5", 807803L>>5},
            {"loop*2", 800928L*2}, {"len(start-loop)", 807803L-800928L},
        };
        long lo=0x180092000L, hi=0x181a08bffL-4;
        byte[] buf=new byte[1<<20];
        for (Object[] t: targets){
            long val=(Long)t[1]; int hits=0; StringBuilder sb=new StringBuilder();
            for (long base=lo; base<hi; base+=buf.length-4){
                int len=(int)Math.min(buf.length, hi-base);
                mem.getBytes(toAddr(base), buf, 0, len);
                for (int i=0;i+4<=len;i++){
                    long v=(buf[i]&0xffL)|((buf[i+1]&0xffL)<<8)|((buf[i+2]&0xffL)<<16)|((buf[i+3]&0xffL)<<24);
                    if (v==val){ if(hits<6) sb.append(String.format(" 0x%x",base+i)); hits++; }
                }
            }
            println(String.format("%-14s = %-10d : %d hits%s", t[0], val, hits, sb));
        }
    }
}
