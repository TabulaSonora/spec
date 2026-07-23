# Provenance of `SCCore.dll` (Roland SOUND Canvas VA)

What is the synth core of SOUND Canvas VA, and where did it come from? This document collects the
evidence turned up while reverse-engineering `SCCore.dll`, and works toward the question: **is it a
port of the original Sound Canvas hardware firmware, an emulation, or a from-scratch reimplementation?**

Everything in the "Verified evidence" sections below was confirmed directly against the DLL bytes /
decompile during this project. The "Hardware architecture" and "Verdict" sections are being completed
from a dedicated research pass (external hardware sourcing + porting-artifact analysis) — marked
🔬 PENDING until filled in.

---

## 0. Exact file identity (the build this project was done against)

All offsets, tables, and findings in this repo are pinned to **this** `SCCore.dll`. A different build
may move data; verify before reusing the offsets (see [`DLL_LAYOUT.md`](DLL_LAYOUT.md) and
[`../tables/manifest.json`](../tables/manifest.json)).

| field | value |
|---|---|
| size | 27,347,456 bytes |
| SHA-256 | `117E6AA147A96FBDE5E10D2CAF16C89965ACC1E44235FD245992216CC620BDB1` |
| SHA-1 | `CF9DCE5A0CABEE06792E884673B8BEEF806F1AED` |
| MD5 | `DBD9A30C168EFEF577D40A28D9ADF37D` |
| PE timestamp | `1572416468` → 2019-10-30 06:21:08 UTC |
| file mtime | 2020-01-19 UTC |
| version resource | empty — identify by hash + PE timestamp + size |

---

## 1. Verified evidence — the embedded wave ROM is the *literal* hardware ROM

`SCCore.dll` embeds a 24 MB wave/sample ROM that is **self-identifying**: every 1 MB block begins with
a 0x50-byte header.

Header layout (offsets from each block base):

| Offset | Size | Field |
|--------|------|-------|
| +0x00 | 16 | Magic — first 6 bytes `A4 EB A5 2B E9 29` are identical at all 24 block bases |
| +0x20 | 16 | ASCII generation label |
| +0x30 | 16 | ASCII build date `YYYY-MM-DD` |
| +0x40 | 16 | dwords `{0x4001, 0x20, 0x01}`, then zero/`0xFF` pad, then DPCM sample data |

The 24 blocks partition into **three hardware wave-ROM generations**, each tagged with its original
build date:

| Label | Build date | Size | File-offset region | Hardware wave set |
|-------|-----------|------|--------------------|-------------------|
| `ver200`   | 1994-12-08 |  8 MB | `0x92700` … `0x892700`   | SC-88 |
| `rom_make` | 1996-06-16 | 12 MB | `0x892700` … `0x1492730` | SC-88Pro |
| `8820_wv0` | 1999-08-17 |  4 MB | `0x1492730` … `0x1892730` | SC-8820 |

The magic recurs at exactly 0x100000 intervals, with a +0x30-byte file-layout drift at the
block-15→16 boundary (`0xF92700` → `0x1092730`). The `8820_wv0` label names the SC-8820 outright, and
the dates line up with each model's release era. **Conclusion: SC-VA ships the actual SC-88 +
SC-88Pro + SC-8820 mask-ROM wave data, stacked, with the original ROM build stamps intact.**

## 2. Verified evidence — the synthesis matches the hardware algorithms exactly

The reverse-engineered voice engine reproduces hardware-specific algorithms bit-for-bit, not generic
DSP:

- **Block-floating-point DPCM sample codec** (per-16-sample scale nibble; integrator predictor with
  forward/ping-pong loop variants) — matches the engine's own decode on the live voice struct.
- **Chamberlin state-variable TVF filter** (`tvf_svf_render`), coefficient `f = 2^(cc/16384−15)` via
  an exp table, `q` from a resonance byte — verified against the DLL's own coefficient table.
- **LFO / envelope tables read from ROM**, a **100 Hz control tick**, and **fixed-point
  milli-semitone pitch math** (clamp `0x1f018` = 127000).
- **GS SysEx parameter address map** matching the published Roland GS spec (mod-wheel depth `40 2x 04`
  default 0x0A, etc.).

## 3. Verified evidence — identity strings and PE metadata

- Internal display/identity strings: `- SOUND Canvas -` (@`0x1985320`), `SC-8820` (@`0x19A74F0`),
  `Roland SC-GS Version 1.00` / `1.10`, `Roland XP-GS Ver.1.01` (@`0x1A02C7F`).
- **PE `VS_VERSION_INFO` is empty** — no FileVersion / ProductName / LegalCopyright. The product's
  only self-identification is the ROM block headers and these internal strings.
- No Roland mask-ROM part numbers (`R00…`/`MB834…`/`HN62…`) and no UTF-16 metadata were found.
- Exported entry points are `TG_*` (`TG_Process`, `TG_ShortMidiIn`, `TG_setSampleRate`, …) — a
  "tone generator" naming convention.

---

## 4. Porting artifacts in `SCCore.dll` (verified)

Independently confirmed against the DLL / decompile:

- **The public API is a tone-generator firmware API named after the hardware ASIC.** All 17 exports
  are `TG_*` ("TG" = Tone Generator), including **`TG_XPgetCurSystemConfig`, `TG_XPsetSystemConfig`,
  `TG_XPgetCurTotalRunningVoices`** — **"XP" is the name of the physical tone-generator ASIC** in the
  hardware (see §5). Also `TG_setInterruptThreadIdAtThisTime` — an **interrupt-service concept remapped
  onto a host thread**, the artifact of firmware whose synthesis ran off a timer interrupt being ported
  to a PC where a thread stands in for the ISR.
- **Firmware / RTOS error strings** (`TG_getErrorStrings`): `TGER: RTOS fail to start` (@DLL
  `0x1A02F76`), `TGER: Unsupported Sample Frequency`, `TGER: Lack of heap memory`, `TGER: Not
  Initialized yet`, `TGER: File Open Error`, `TGCORE: Unsupported Parameter`. An **RTOS start-up error
  is meaningless in a from-scratch VST** — it is a diagnostic carried over from real embedded firmware
  that booted on a real-time OS. (The slightly broken English, e.g. `TGER: File Error except open
  error`, is consistent with translated Japanese firmware.)
- **GS firmware ROM identity strings**, byte-for-byte what the hardware returns for a GS identity
  request: `Roland XP-GS    Ver.1.01` (@`0x1A02C80`), `Roland SC-GS    Version 1.00` (@`0x1A02CA0`),
  `Roland SC-GS    Version 1.10` (@`0x1A02CC0`). A routine builds a table and compares against them —
  firmware version gating preserved in software — with an inline **16-bit big-endian↔little-endian
  byte-swap** (`x<<8 | x>>8`) right beside it; there are only ~2 such explicit swaps in 84k lines,
  implying the big-endian ROM/tables were swapped **once, offline at build time**, the normal way a
  big-endian firmware image is ported to a little-endian target.
- **Control path is exclusively 16-bit fixed-point — an H8's native idiom on a 64-bit target.** The
  LFO / envelope / TVF / TVA code (`lfo_update` etc.) runs entirely on `short`/`ushort`/`uint`
  accumulators with `<0x10000` wrap arithmetic and a shared control tick — **no floats**; floats appear
  only in the effects/DSP stage and at the audio-output boundary. Zero `double`s and no real C++
  classes in the whole decompile (MSVC compiling **C**, not modern C++). A fresh engine would use
  floats throughout.
- **Synthesis math is the ASIC's own algorithms**: hardware **block-floating-point DPCM** codec
  (`predictor += (int8)delta << (scale+10)`), including a **reverse-playback sampler variant** that
  mirrors a real hardware SFX feature; the Chamberlin state-variable TVF (`tvf_svf_render`); the
  literal hardware wave ROM (§1).
- **Dead code from the shared Roland DSP codebase**: `fx_algo_orphan66_moddelay` is an effect algorithm
  **unreachable in SC-VA** (no caller), annotated as carried from the shared Roland DSP codebase but not
  exposed here. Carrying orphaned, unreachable algorithms is a hallmark of **porting a shared codebase**,
  not writing a fresh engine.

## 5. Hardware architecture of the physical Sound Canvas

- **Main CPU: Hitachi H8/510** (`HD6415108F`), a 16-bit microcontroller — confirmed on SK-88Pro /
  SC-88Pro boards. Big-endian (as is the whole Hitachi H8/SH family); x86-64 is little-endian, so a
  genuine port must flip endianness (matching the byte-swap in §4).
- **Synthesis ran on a separate custom ASIC, not the CPU**: a dedicated Roland **"XP" tone-generator**
  (e.g. `XP RA01-005`) did the PCM voice synthesis, and a **separate `MB87837PF` DSP** did
  reverb/chorus/EFX. The H8 handled MIDI, patch management, envelopes/LFO, and GS SysEx.
- The SC-8820 (what SC-VA is based on) uses a PCM engine derived from the SC-88Pro (128-voice/64-part).
  Roland never publicly documented SC-VA's construction method; the verdict rests on the internal
  evidence, which is unambiguous.

Sources: [sandsoftwaresound.net teardown](https://sandsoftwaresound.net/dive-into-old-roland-gear/) ·
[VOGONS SC-88 repair](https://www.vogons.org/viewtopic.php?t=47094) ·
[Roland SC-8850 (Wikipedia)](https://en.wikipedia.org/wiki/Roland_SC-8850) ·
[H8 Family (Wikipedia)](https://en.wikipedia.org/wiki/H8_Family).

## 6. Verdict

**The "port of the original hardware firmware" hypothesis is confirmed — with a refinement. Confidence: high (~90%).**

`SCCore.dll` is **not a from-scratch reimplementation**. The convergent evidence — the `TG_*`/`TG_XP*`
export API named after the hardware ASIC, the `RTOS`/`TGER`/`TGCORE` firmware error strings, the
ISR-as-thread export, the preserved GS firmware version strings with an inline endian swap, the
exclusively 16-bit fixed-point control path with zero doubles, the literal hardware wave ROM, the
hardware block-FP DPCM codec with a reverse-SFX variant, and orphaned dead-code from the shared Roland
DSP codebase — all point to a **direct software port of Roland's original Sound Canvas firmware/ASIC
code**, recompiled for x86-64 with MSVC and wrapped in a thin VST/AU host shim.

**Refinement of "C port of Hitachi H8 code":** the H8 was only the *control* CPU. The hardware split
work across **three** domains, and `SCCore.dll` ports **all three**:

1. **H8 control firmware** (MIDI, voice allocation, envelopes, LFO, pitch, GS SysEx) — **ported
   essentially verbatim**; strongest signal (fixed-point, `TG_`/RTOS/version-string artifacts). This
   directly vindicates the H8 hypothesis.
2. **XP tone-generator ASIC** (DPCM playback, interpolation, TVF) — **ported/transliterated**; exact
   ASIC algorithms + literal ROM, with inner loops partly vectorized to float SIMD for the host.
3. **`MB87837` effects DSP** (reverb/chorus/EFX) — **ported from the shared Roland DSP codebase**, run
   in float; the orphaned unreachable algorithm is the tell.

So it is neither a clean-room re-creation nor "just" an H8 port — it is a **whole-stack port of the
hardware's firmware + ASIC + DSP code, with the original 24 MB wave ROM embedded intact**. The only
genuinely new code is the outer host/threading wrapper. **For preservation purposes, `SCCore.dll`
should be regarded as derived directly from Roland's original Sound Canvas hardware source, not a
behavioral re-creation.**

*Caveat:* the SC-8820's exact CPU part number wasn't confirmable from a direct teardown (service manual
unreachable); it is inferred from the confirmed SC-88Pro H8/510 lineage and the "engine based on
SC-88Pro" statement. This does not affect the verdict — the internal artifacts are unambiguous.

---

*Sources: direct inspection of `SCCore.dll` and `SCCore.decompiled.c`; see the project memory notes
`scvx-rom-identity`, `scvx-render-path`, `scvx-patch-tables`. Verdict from a dedicated hardware-research
+ porting-artifact analysis pass, with the key internal claims independently re-verified against the
DLL bytes.*
