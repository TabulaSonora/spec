# Tabula Sonora — a spec for the Roland Sound Canvas VA voice

A readable, cross-platform-ready **specification and reference implementation** of the Roland Sound
Canvas VA synth voice, reverse-engineered from `SCCore.dll` and validated against the DLL's own
internal state (not fitted to audio).

The goal: given the (version-pinned) `SCCore.dll`, anyone — human or agent — can build an engine that
reads the ROM + tables straight from the DLL and plays MIDI from an SMF file or a live keyboard. The
Python here is a **proof-of-concept reference**, not the product; it exists so an implementation in any
language has something exact to check against.

This is a preservation / interoperability effort on a discontinued product.

## What's here

| Path | What it is |
|---|---|
| `scvx_engine.py` | The synth voice: block-FP ADPCM codec → 4-tap resampler → TVF state-variable filter → TVA/pitch envelopes → LFO → pan/mix. Built from the reversed tables; no DLL at render time. |
| `scvx_directory.py` / `scvx_partials.py` | Patch resolution (program+note+vel → wave + ROM coords) and per-partial synthesis-parameter decode. |
| `scvx_sequencer.py` | Playback layer: SMF parsing + per-part controllers over time (CC7/11/1/10/64, bend, RPN tune, drum kits). |
| `scvx_reverb.py` / `scvx_chorus.py` / `scvx_delay.py` | The three GS **send** effects (reverb/chorus + system delay), transcribed DSP networks. |
| `tables/manifest.json` | The machine-readable **map**: every table's byte-exact DLL offset, size, dtype, symbol, purpose. The tables themselves (`tables/*.bin`, `*.txt`) are DLL-derived and **not shipped** — extract them locally (see below). |
| `docs/` | The reverse-engineering record — see below. |
| `tools/extract_tables.py` | Regenerates `tables/*.bin` from your own `SCCore.dll` using the manifest offsets (verifies the DLL hash first). |
| `tools/decoder/` | `scdec`, a C# harness that drives the real DLL for A/B ground truth (also produces the `tables/*.txt` effect-type dumps). |
| `tools/gen_manifest.py` | Regenerates `tables/manifest.json` (and DLL hashes) from any `SCCore.dll`. |
| `tools/ghidra_scripts/` | Headless Ghidra scripts that produce/annotate the decompile. |

## Start here

1. **[docs/DLL_LAYOUT.md](docs/DLL_LAYOUT.md)** — the pinned DLL identity (hashes, PE timestamp) and how
   every table/ROM/directory region maps to a raw file offset. Everything the engine needs lives inside
   `SCCore.dll`; the `tables/*.bin` cache is fully derivable from it.
2. **[docs/FINDINGS.md](docs/FINDINGS.md)** — the master log: every reversed algorithm, table, and
   offset, with confidence tags.
3. **[docs/PROVENANCE.md](docs/PROVENANCE.md)** — what `SCCore.dll` is (a whole-stack port of the
   SC-88/88Pro/8820 hardware, original 24 MB wave ROM embedded intact) and the exact build this targets.
4. **[docs/SYMBOLS.md](docs/SYMBOLS.md)** / **[docs/GLOSSARY.md](docs/GLOSSARY.md)** — symbol map and
   terminology.
5. **[docs/COMPARING_RENDERS.md](docs/COMPARING_RENDERS.md)** — how to judge a reimplementation's
   audio against the DLL's, and why comparing samples answers the wrong question.
   `tools/compare_envelope.py` implements it.

## Getting it running

`SCCore.dll` and everything derived byte-for-byte from it (the wave ROM, the `tables/*.bin`, the Ghidra
decompile) are **copyrighted and not included**. The repo ships only original work — the engine code,
the docs, and `tables/manifest.json` (the offset map). To run the reference engine:

1. Obtain the exact `SCCore.dll` build identified in [`docs/DLL_LAYOUT.md`](docs/DLL_LAYOUT.md)
   (size 27,347,456; SHA-256 `117E6AA1…C620BDB1`; PE timestamp 2019-10-30).
2. Extract the tables from it:
   ```
   python tools/extract_tables.py "C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"
   ```
   This verifies the DLL hash and writes `tables/*.bin`. The effect-type dumps (`tables/*.txt`) come
   from building `tools/decoder` and running `scdec revdump`/`chodump` against the same DLL.
3. The engine reads the DLL directly at render time for the wave ROM, so keep it at the path above (or
   edit the path in `scvx_engine.py`).

A different DLL build may move tables — re-run `tools/gen_manifest.py` to re-derive the offsets.

## Scope

The core voice + GS **send** effects are specified. The 66-algorithm **insertion EFX** subsystem is
intentionally out of scope here — it belongs downstream in a concrete engine, layered on top of this.

## Implementations

- [**DotNetAdministravit**](https://github.com/TabulaSonora/DotNetAdministravit) — a managed C#
  engine built to this spec. Renders MIDI to audio at ~15× realtime, reading `SCCore.dll` as a data
  file rather than loading it as code.

## Licence

BSD 3-Clause — see [`LICENSE`](LICENSE). That covers this repository's own work: the docs, the Python
reference, the tooling, and `tables/manifest.json`. It grants nothing in Roland's DLL or the data
inside it; see [`NOTICE.md`](NOTICE.md).

The licence is permissive so that an implementation can be built from this spec under any licence,
including the GPL.
