// Headless -preScript: keep auto-analysis off the 24 MB of embedded wave ROM.
//
// SCCore.dll is ~27 MB of which ~0.6 MB is .text -- the rest is wave ROM and tables. The analyzers
// that sweep initialized memory (string search, embedded media, data-shape guessing) spend all their
// time in the ROM and find nothing. Disable those; keep everything that builds code and the
// instruction->data references DumpXrefs.java depends on.
//
//@category SCVA
import ghidra.app.script.GhidraScript;
import ghidra.framework.options.Options;
import ghidra.program.model.listing.Program;

public class TuneAnalysis extends GhidraScript {
    // Analyzers that scan bulk data rather than code.
    private static final String[] OFF = {
        "ASCII Strings",
        "Embedded Media",
        "Create Address Tables",
        "Decompiler Parameter ID",
        "Scalar Operand References",
    };

    @Override
    public void run() throws Exception {
        Options o = currentProgram.getOptions(Program.ANALYSIS_PROPERTIES);
        for (String name : OFF) {
            for (String key : o.getOptionNames()) {
                if (key.equals(name) || key.startsWith(name + ".")) {
                    if (o.getType(key) == ghidra.framework.options.OptionType.BOOLEAN_TYPE) {
                        o.setBoolean(key, false);
                        println("TuneAnalysis: disabled " + key);
                    }
                }
            }
        }
    }
}
