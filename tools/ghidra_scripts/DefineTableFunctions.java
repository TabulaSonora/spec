import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSet;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.SourceType;

import java.util.ArrayList;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/**
 * Defines the functions Ghidra's recursive descent cannot reach.
 *
 * <p>SCCore.dll dispatches large parts of its control path through data pointer tables — the
 * envelope stage handlers, the per-controller MIDI handlers, the effect algorithms. Nothing
 * <em>calls</em> those targets, so auto-analysis never disassembles them and they are simply absent
 * from the decompile. This sweeps every initialized data block for 8-byte values that land on
 * executable memory and defines a function at each one, then repeats until it reaches closure,
 * since a newly defined function's own body routinely references further tables.
 *
 * <p>A second pass picks up code stranded in .text with no pointer to it at all: runs of undefined
 * bytes that begin just after the <code>0xCC</code> padding following a defined function, which is
 * what an unreferenced function looks like in an MSVC image.
 *
 * <p>Usage (headless): <code>-postScript DefineTableFunctions.java</code>
 */
public class DefineTableFunctions extends GhidraScript {

    /** Alignment MSVC gives function entries; anything else is a mis-parse, not a function. */
    private static final int ENTRY_ALIGNMENT = 16;

    /** Give up after this many sweeps even if the last one still found something. */
    private static final int MAX_PASSES = 12;

    @Override
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();

        List<MemoryBlock> dataBlocks = new ArrayList<>();
        AddressSet executable = new AddressSet();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized()) {
                continue;
            }
            if (b.isExecute()) {
                executable.addRange(b.getStart(), b.getEnd());
            } else {
                dataBlocks.add(b);
            }
        }

        println("DefineTableFunctions: executable " + executable.getMinAddress() + ".."
                + executable.getMaxAddress() + ", " + dataBlocks.size() + " data blocks");

        Set<Address> tried = new HashSet<>();
        int createdTotal = 0;

        for (int pass = 1; pass <= MAX_PASSES; pass++) {
            if (monitor.isCancelled()) {
                break;
            }

            List<Address> targets = new ArrayList<>();
            for (MemoryBlock b : dataBlocks) {
                collectPointers(mem, b, executable, tried, targets);
            }

            int created = defineAll(targets);
            createdTotal += created;
            println("  pass " + pass + ": " + targets.size() + " new candidates, " + created + " defined");

            if (created == 0) {
                break;
            }

            // Let the analyzer follow what the new bodies reference before the next sweep.
            analyzeChanges(currentProgram);
        }

        int stranded = defineStrandedCode(executable);
        println("DefineTableFunctions: " + createdTotal + " from pointer tables, " + stranded
                + " stranded in code; total functions now "
                + currentProgram.getFunctionManager().getFunctionCount());
    }

    /** Collects pointer-sized values in one block that land on executable memory. */
    private void collectPointers(Memory mem, MemoryBlock block, AddressSet executable,
            Set<Address> tried, List<Address> targets) throws Exception {
        Address start = block.getStart();
        long length = block.getSize();

        for (long offset = 0; offset + 8 <= length; offset += 8) {
            if ((offset & 0xFFFFF) == 0 && monitor.isCancelled()) {
                return;
            }

            long value;
            try {
                value = mem.getLong(start.add(offset));
            } catch (Exception e) {
                continue;
            }

            if (value == 0) {
                continue;
            }

            Address target;
            try {
                target = toAddr(value);
            } catch (Exception e) {
                continue;
            }

            if (target == null || !executable.contains(target)) {
                continue;
            }

            // A real entry point is aligned and starts a function we have not already defined.
            if ((value % ENTRY_ALIGNMENT) != 0 || !tried.add(target)) {
                continue;
            }

            if (getFunctionAt(target) == null) {
                targets.add(target);
            }
        }
    }

    /** Disassembles and defines a function at each address, skipping anything that will not parse. */
    private int defineAll(List<Address> targets) {
        int created = 0;

        for (Address target : targets) {
            if (monitor.isCancelled()) {
                break;
            }

            // Inside an existing function: a jump table entry or a tail-call target, not a new one.
            Function containing = getFunctionContaining(target);
            if (containing != null && !containing.getEntryPoint().equals(target)) {
                continue;
            }

            Instruction at = getInstructionAt(target);
            if (at == null) {
                if (!disassemble(target)) {
                    continue;
                }

                at = getInstructionAt(target);
                if (at == null) {
                    continue;
                }
            }

            try {
                if (createFunction(target, null) != null) {
                    created++;
                }
            } catch (Exception e) {
                // Overlapping or unparseable — leave it for the stranded-code pass.
            }
        }

        return created;
    }

    /**
     * Defines functions in code the pointer sweep did not reach.
     *
     * <p>Walks executable memory for undefined bytes sitting on an entry alignment boundary and
     * preceded by <code>0xCC</code> padding — the shape of a function MSVC emitted but nothing in
     * the image references by address.
     */
    private int defineStrandedCode(AddressSet executable) throws Exception {
        int created = 0;
        Address address = executable.getMinAddress();
        Address end = executable.getMaxAddress();

        while (address != null && address.compareTo(end) < 0) {
            if (monitor.isCancelled()) {
                break;
            }

            if (getFunctionContaining(address) != null || getInstructionAt(address) != null) {
                address = address.add(1);
                continue;
            }

            if ((address.getOffset() % ENTRY_ALIGNMENT) != 0) {
                address = address.add(1);
                continue;
            }

            byte previous;
            byte first;
            try {
                previous = getByte(address.subtract(1));
                first = getByte(address);
            } catch (Exception e) {
                address = address.add(1);
                continue;
            }

            // Padding before it, and not padding itself.
            if (previous != (byte) 0xCC || first == (byte) 0xCC) {
                address = address.add(ENTRY_ALIGNMENT);
                continue;
            }

            if (disassemble(address) && getInstructionAt(address) != null) {
                try {
                    if (createFunction(address, null) != null) {
                        created++;
                    }
                } catch (Exception e) {
                    // Not a function after all.
                }
            }

            address = address.add(ENTRY_ALIGNMENT);
        }

        return created;
    }
}
