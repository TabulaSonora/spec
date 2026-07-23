import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Fine boundary scan + raw byte peek + resolve TG_initialize's 8 copy-blob sizes. */
public class RomProbe extends GhidraScript {
    String hex(long off, int n) throws Exception {
        byte[] b=new byte[n]; currentProgram.getMemory().getBytes(toAddr(off),b,0,n);
        StringBuilder s=new StringBuilder();
        for(int i=0;i<n;i++) s.append(String.format("%02x ",b[i]&0xff));
        return s.toString();
    }
    double ent(long off,int n) throws Exception {
        byte[] b=new byte[n]; currentProgram.getMemory().getBytes(toAddr(off),b,0,n);
        int[] h=new int[256]; for(byte x:b) h[x&0xff]++;
        double e=0; for(int c:h) if(c>0){double p=(double)c/n; e-=p*Math.log(p)/Math.log(2);} return e;
    }
    @Override public void run() throws Exception {
        println("=== fine entropy scan around the 24MB boundary (16KB windows) ===");
        for (long off=0x181882000L; off<0x1818e2000L; off+=0x4000)
            println(String.format("  0x%09x  ent=%.2f", off, ent(off,0x4000)));
        println("=== raw bytes at sample-region start 0x180092000 ==="); println("  "+hex(0x180092000L,48));
        println("=== raw bytes mid-sample 0x181000000 ==="); println("  "+hex(0x181000000L,48));
        println("=== raw bytes at table region 0x181895190 (dispatch) ==="); println("  "+hex(0x181895190L,48));

        // TG_initialize copies 8 blobs from a table at PTR_FUN_181a0fa18. Resolve each triple.
        println("=== TG_initialize 8-blob table @0x181a0fa18 (stride 0x20: [size_fn][?][src][dst]) ===");
        long tbl=0x181a0fa18L;
        for (int i=0;i<8;i++){
            long rec=tbl + (long)i*0x20;
            long p0=currentProgram.getMemory().getLong(toAddr(rec));
            long p1=currentProgram.getMemory().getLong(toAddr(rec+8));
            long p2=currentProgram.getMemory().getLong(toAddr(rec+16));
            long p3=currentProgram.getMemory().getLong(toAddr(rec+24));
            println(String.format("  rec%d: %012x %012x %012x %012x", i,p0,p1,p2,p3));
        }
    }
}
