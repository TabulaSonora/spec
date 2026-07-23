import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.symbol.*;
import ghidra.program.model.listing.*;

/** Finds who writes the wave-ROM base pointers, and characterizes .rdata content. */
public class FindRomInit extends GhidraScript {
    @Override
    public void run() throws Exception {
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (String s : new String[]{"181a18ef0","181a11a68"}) {
            Address t = toAddr(Long.parseLong(s,16));
            println("=== refs to 0x"+s+" ===");
            for (Reference r : rm.getReferencesTo(t)) {
                Address from = r.getFromAddress();
                Function fn = getFunctionContaining(from);
                println(String.format("  %s  %s  in %s", from, r.getReferenceType(),
                    fn!=null?fn.getName()+"@"+fn.getEntryPoint():"?"));
            }
        }
        // Coarse content profile of .rdata: entropy + int16 smoothness per 256KB window
        Address base = toAddr(0x180092000L), end = toAddr(0x181a08bffL);
        long start = base.getOffset(), stop = end.getOffset();
        int win = 262144;
        println("=== .rdata profile (256KB windows): offset  entropy_bits  meanAbsInt16Delta ===");
        byte[] buf = new byte[win];
        for (long off = start; off < stop; off += win) {
            int len = (int)Math.min(win, stop-off);
            currentProgram.getMemory().getBytes(toAddr(off), buf, 0, len);
            int[] hist = new int[256];
            for (int i=0;i<len;i++) hist[buf[i]&0xff]++;
            double ent=0; for(int c:hist) if(c>0){double p=(double)c/len; ent-=p*(Math.log(p)/Math.log(2));}
            long sd=0; int ns=0; short prev=0; boolean first=true;
            for (int i=0;i+1<len;i+=2){ short v=(short)((buf[i]&0xff)|(buf[i+1]<<8)); if(!first){sd+=Math.abs(v-prev);ns++;} prev=v; first=false; }
            double mad = ns>0 ? (double)sd/ns : 0;
            println(String.format("  0x%09x  %.2f  %.0f", off, ent, mad));
        }
    }
}
