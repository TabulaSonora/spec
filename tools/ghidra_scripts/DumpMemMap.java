import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.MemoryBlock;

/** Lists memory blocks (PE sections) with sizes, largest first, to locate embedded sample data. */
public class DumpMemMap extends GhidraScript {
    @Override
    public void run() throws Exception {
        MemoryBlock[] blocks = currentProgram.getMemory().getBlocks();
        java.util.Arrays.sort(blocks, (a,b) -> Long.compare(b.getSize(), a.getSize()));
        println("=== MEMORY BLOCKS (by size) ===");
        for (MemoryBlock b : blocks) {
            println(String.format("%-12s %s - %s  size=%,d (%.1f MB)  %c%c%c %s",
                b.getName(), b.getStart(), b.getEnd(), b.getSize(), b.getSize()/1048576.0,
                b.isRead()?'r':'-', b.isWrite()?'w':'-', b.isExecute()?'x':'-',
                b.isInitialized()?"init":"uninit"));
        }
        // where do the wave rom bases live (as pointer variables)?
        for (String s : new String[]{"181a18ef0","181a11a68","181a1b5b8","181a6fb60"}) {
            var a = toAddr(Long.parseLong(s,16));
            var blk = currentProgram.getMemory().getBlock(a);
            println("ptr-var 0x"+s+" is in block: "+(blk!=null?blk.getName():"?"));
        }
    }
}
