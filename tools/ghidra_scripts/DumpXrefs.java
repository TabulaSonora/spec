// Dump every function's ordered data references and call targets, as JSON.
//
// The point is cross-build table location. The repo's table offsets are pinned to one SCCore.dll
// build, and other builds reorder .rdata freely (docs/DLL_VERSIONS.md), so a table cannot be found
// in a new build by offset arithmetic and -- for short ramp-like curves -- not reliably by content
// search either. What *is* stable is the code: the instruction that indexes a table sits in the same
// place in the same function. Dump that structure from both builds and the correspondence falls out
// (tools/match_xrefs.py does the matching).
//
// Usage (headless):  -postScript DumpXrefs.java <out.json>
// Defaults to $SCVX_TABLES_DIR/xrefs.json, else ./xrefs.json.
//
//@category SCVA
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.MemoryBlock;
import java.io.*;
import java.util.*;

public class DumpXrefs extends GhidraScript {

    private static String esc(String s) {
        return s == null ? "" : s.replace("\\", "\\\\").replace("\"", "\\\"");
    }

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String out;
        if (args.length > 0) {
            out = args[0];
        } else {
            String dir = System.getenv("SCVX_TABLES_DIR");
            out = (dir == null ? "." : dir) + File.separator + "xrefs.json";
        }
        File f = new File(out);
        if (f.getParentFile() != null) f.getParentFile().mkdirs();

        Listing listing = currentProgram.getListing();
        long imageBase = currentProgram.getImageBase().getOffset();

        PrintWriter w = new PrintWriter(new BufferedWriter(new FileWriter(f), 1 << 20));
        w.println("{");
        w.printf("\"program\": \"%s\",%n", esc(currentProgram.getName()));
        w.printf("\"image_base\": \"%x\",%n", imageBase);

        // Section map, so the matcher can turn a VA into a file offset without re-parsing the PE.
        w.println("\"blocks\": [");
        boolean firstBlock = true;
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!firstBlock) w.println(",");
            firstBlock = false;
            w.printf("  {\"name\": \"%s\", \"start\": \"%x\", \"size\": %d, \"init\": %b}",
                     esc(b.getName()), b.getStart().getOffset(), b.getSize(), b.isInitialized());
        }
        w.println("\n],");

        w.println("\"functions\": [");
        FunctionIterator fit = currentProgram.getFunctionManager().getFunctions(true);
        int nfunc = 0, nref = 0;
        boolean firstFunc = true;
        while (fit.hasNext() && !monitor.isCancelled()) {
            Function fn = fit.next();
            StringBuilder refs = new StringBuilder();
            StringBuilder calls = new StringBuilder();
            int idx = 0, nr = 0, nc = 0;

            InstructionIterator iit = listing.getInstructions(fn.getBody(), true);
            while (iit.hasNext()) {
                Instruction ins = iit.next();
                for (Reference r : ins.getReferencesFrom()) {
                    RefType t = r.getReferenceType();
                    Address to = r.getToAddress();
                    if (t.isCall()) {
                        if (nc++ > 0) calls.append(", ");
                        calls.append('"').append(Long.toHexString(to.getOffset())).append('"');
                    } else if (t.isData() && !t.isWrite()) {
                        // Read/data refs only: these are the table indexing sites.
                        if (nr++ > 0) refs.append(",\n");
                        refs.append(String.format(
                            "    {\"i\": %d, \"at\": \"%x\", \"to\": \"%x\", \"m\": \"%s\", \"t\": \"%s\"}",
                            idx, ins.getAddress().getOffset(), to.getOffset(),
                            esc(ins.getMnemonicString()), esc(t.getName())));
                        idx++;
                    } else if (t.isData()) {
                        if (nr++ > 0) refs.append(",\n");
                        refs.append(String.format(
                            "    {\"i\": %d, \"at\": \"%x\", \"to\": \"%x\", \"m\": \"%s\", \"t\": \"%s\", \"w\": 1}",
                            idx, ins.getAddress().getOffset(), to.getOffset(),
                            esc(ins.getMnemonicString()), esc(t.getName())));
                        idx++;
                    }
                }
            }
            nref += nr;

            if (!firstFunc) w.println(",");
            firstFunc = false;
            w.printf("  {\"entry\": \"%x\", \"name\": \"%s\", \"size\": %d,%n",
                     fn.getEntryPoint().getOffset(), esc(fn.getName()), fn.getBody().getNumAddresses());
            w.printf("   \"calls\": [%s],%n", calls);
            w.printf("   \"refs\": [%s]}", nr == 0 ? "" : "\n" + refs + "\n   ");
            nfunc++;
        }
        w.println("\n]}");
        w.close();
        println(String.format("DumpXrefs: %d functions, %d data refs -> %s", nfunc, nref, out));
    }
}
