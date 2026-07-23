import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileOptions;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.decompiler.DecompiledFunction;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;

import java.io.BufferedWriter;
import java.io.FileWriter;
import java.io.PrintWriter;

/**
 * Decompiles every function in the current program to a single .c file.
 * Usage (headless): -postScript DecompileToC.java <output.c>
 */
public class DecompileToC extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        String outPath = (args.length > 0) ? args[0] : "decompiled.c";

        DecompInterface ifc = new DecompInterface();
        ifc.setOptions(new DecompileOptions());
        if (!ifc.openProgram(currentProgram)) {
            println("DecompileToC: failed to open program: " + ifc.getLastMessage());
            return;
        }

        int total = 0, ok = 0, failed = 0;
        try (PrintWriter pw = new PrintWriter(new BufferedWriter(new FileWriter(outPath)))) {
            pw.println("// Decompilation of " + currentProgram.getName());
            pw.println("// Image base: " + currentProgram.getImageBase());
            pw.println("// Language: " + currentProgram.getLanguageID());
            pw.println("// Compiler: " + currentProgram.getCompilerSpec().getCompilerSpecID());
            pw.println();

            FunctionManager fm = currentProgram.getFunctionManager();
            for (Function f : fm.getFunctions(true)) {
                if (monitor.isCancelled()) break;
                total++;
                DecompileResults res = ifc.decompileFunction(f, 120, monitor);
                if (res != null && res.decompileCompleted()) {
                    DecompiledFunction df = res.getDecompiledFunction();
                    if (df != null && df.getC() != null) {
                        pw.println("/* " + f.getName() + " @ " + f.getEntryPoint()
                                + (f.isThunk() ? " (thunk)" : "") + " */");
                        pw.println(df.getC());
                        pw.println();
                        ok++;
                        continue;
                    }
                }
                String msg = (res != null) ? res.getErrorMessage() : "null result";
                pw.println("// FAILED: " + f.getName() + " @ " + f.getEntryPoint() + "  (" + msg + ")");
                pw.println();
                failed++;
                if (total % 250 == 0) {
                    println("DecompileToC: " + total + " functions processed...");
                }
            }
        }
        ifc.dispose();
        println("DecompileToC: wrote " + ok + " OK, " + failed + " failed, " + total + " total -> " + outPath);
    }
}
