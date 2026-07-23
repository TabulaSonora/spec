import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;

/** Scan .rdata/.data for the wave-directory entry fingerprint {loop_start,end,start} per instrument. */
public class FindWaveTable extends GhidraScript {
    // {loop_start, end, start, wave_ctrl, label}
    long[][] waves = {
        {800928, 803508, 807803, 0x8006L}, // flute
        {478944, 519459, 538334, 0x8008L}, // piano
        {650818, 668976, 669219, 0x800DL}, // bass
        {733600, 735734, 735854, 0x8003L}, // marimba
        {399566, 369825, 369825, 0x8804L}, // reverse cymbal (loop>start)
    };
    String[] labels = {"flute","piano","bass","marimba","revcymbal"};

    @Override public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        // scan the whole image for 3 consecutive LE u32 == {loop,end,start}
        long start = 0x180092000L, end = 0x181a08bffL - 16;
        byte[] buf = new byte[1<<20];
        for (int wi=0; wi<waves.length; wi++){
            long L=waves[wi][0], E=waves[wi][1], S=waves[wi][2], WC=waves[wi][3];
            int hits=0;
            for (long base=start; base<end; base+=buf.length-16){
                int len=(int)Math.min(buf.length, end-base);
                mem.getBytes(toAddr(base), buf, 0, len);
                for (int i=0;i+16<=len;i+=4){
                    long v0=u32(buf,i), v1=u32(buf,i+4), v2=u32(buf,i+8);
                    if (v0==L && v1==E && v2==S){
                        long v3=u32(buf,i+12);
                        println(String.format("%-9s @ 0x%09x : loop=%d end=%d start=%d next=0x%x (wc? 0x%x)",
                            labels[wi], base+i, v0,v1,v2, v3, WC));
                        hits++;
                    }
                }
            }
            if (hits==0) println(labels[wi]+": NOT FOUND as {loop,end,start}");
        }
    }
    long u32(byte[] b,int i){ return (b[i]&0xffL)|((b[i+1]&0xffL)<<8)|((b[i+2]&0xffL)<<16)|((b[i+3]&0xffL)<<24); }
}
