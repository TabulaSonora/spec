import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

/** Dump melodic tone table: name (header ASCII) + partial multisample indices. */
public class DumpToneNames extends GhidraScript {
    int u(byte b){ return b&0xff; }
    int u16(byte[] d,int o){ return u(d[o])|(u(d[o+1])<<8); }
    @Override public void run() throws Exception {
        long base = 0x1818f2810L; int stride=0x100, count=800;
        byte[] t=new byte[stride];
        // find name field length: scan header for the printable run
        for (int i=0;i<count;i++){
            Address a=toAddr(base+(long)i*stride);
            try { currentProgram.getMemory().getBytes(a,t,0,stride); } catch(Exception e){ break; }
            StringBuilder nm=new StringBuilder();
            for (int j=0;j<0x18;j++){ int c=u(t[j]); if(c>=0x20&&c<0x7f) nm.append((char)c); else break; }
            String name=nm.toString().trim();
            if (name.length()<2) continue;   // skip empty/non-tone slots
            int ms0=u16(t,0x24+2), ms1=u16(t,0x24+0x6e+2);
            int kc0=u(t[0x24+4]), kc1=u(t[0x24+0x6e+4]);
            println(String.format("tone#%-4d %-14s msamp[%d,%d] keyCenter[%d,%d]", i, name, ms0, ms1, kc0, kc1));
        }
    }
}
