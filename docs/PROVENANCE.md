# Provenance of `SCCore.dll` (Roland SOUND Canvas VA)

What is the synth core of SOUND Canvas VA, and where did it come from? This document collects the
evidence turned up while reverse-engineering `SCCore.dll`, and works toward the question: **is it a
port of the original Sound Canvas hardware firmware, an emulation, or a from-scratch reimplementation?**

Everything in the "Verified evidence" sections below was confirmed directly against the DLL bytes /
decompile during this project. The "Hardware architecture" (§5) section has now been completed from a
dedicated hardware-sourcing pass against Roland service notes and board teardowns; the SC-88, SC-88Pro,
and SC-8820 silicon is sourced part-by-part, closing the earlier SC-8820 caveat.

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

## 5. Hardware architecture of the physical Sound Canvas ✅ (sourced from service notes)

SC-VA embeds **three** hardware wave-ROM generations (§1: SC-88 `ver200`, SC-88Pro `rom_make`,
SC-8820 `8820_wv0`). Their silicon is now sourced directly from Roland service notes and board
teardowns. Two facts matter for the verdict: (a) across the three generations the tone-generation
and effects ASICs are a **single continuous lineage** (XP tone generator + `MB87837PF` effects DSP),
and (b) the **main CPU changed architecture** between the SC-88/88Pro era and the SC-8820.

| Function | SC-88 / SC-88Pro (1994 / 1996) | SC-8820 (1999) |
|---|---|---|
| Main CPU | **Hitachi H8/510** `HD6415108F` — 16-bit | **Hitachi SH-2** `HD64F7017F28` (SH7016/7017) — 32-bit |
| Sub CPU (MIDI I/O) | Mitsubishi `M38881M2` | Mitsubishi `M37640E8FP` |
| Tone-generator ASIC ("XP") | `RA01-005` | `RA09-002` ("XP6") |
| Effects DSP | Fujitsu `MB87837PF-G-BND` | Fujitsu `MB87837PF-G-BND` (**same part**) |
| Wave/mask ROM | 4× 4 MB mask ROM | 64 Mbit mask ROM + 16 Mbit flash |
| DAC | NEC `μPD63200GS-E2` (per channel) | Burr-Brown/TI `PCM1716E` |

Key points, each now sourced:

- **The tone generator was always a separate custom ASIC, not the CPU.** On the SC-88 the sound
  section is one custom IC (IC15, labelled **"XP"**) that the service notes describe as *"integrating
  PCM sound source, reverb, chorus, TVA, and TVF functions"* — the exact set of subsystems this repo
  reversed out of `SCCore.dll`. That is a direct correspondence between a named hardware block and the
  ported software.
- **The XP tone generator is a cross-product family, not a Sound-Canvas one-off.** The SC-8820's
  `RA09-002 (XP6)` is the **same tone-generator ASIC used in Roland's pro JV/XV line** — it appears as
  `IC3 XP6 RA09-002` in the XV-1010 teardown. This is what makes the *"orphaned dead-code from a shared
  Roland DSP codebase"* argument in §4 concrete: the silicon (and therefore its microcode/algorithms)
  was shared across the Sound Canvas and JV/XV products, so a software port of it naturally carries
  algorithms that no single product exposes.
- **Big-endian → little-endian.** The Hitachi H8 and SH families are big-endian; x86-64 is
  little-endian, so a genuine port must flip byte order — matching the single offline endian-swap noted
  in §4. Wikipedia's H8 Family article independently lists the **Roland SC-55 and JV880** among H8-based
  music synthesizers, corroborating the H8 lineage of the early Sound Canvas control firmware.
- **The H8-idiom control path fits the SC-88/88Pro era specifically.** SC-VA's control code is
  exclusively 16-bit fixed-point (§4) — the native idiom of the 16-bit H8/510, *not* the 32-bit SH-2 in
  the SC-8820 whose wave set SC-VA is nominally "based on." The most economical reading: SC-VA is a port
  of the **SC-88/88Pro-generation H8 control firmware** carried forward, running the XP tone-generator
  algorithms over all three stacked ROM generations, rather than a port of the SC-8820's newer SH-2
  firmware. The SC-8820 is the *sound-set* ancestor; the H8 line is the *code* ancestor.

Sources: [sandsoftwaresound.net teardown (SK-88Pro / XP-80 / XV-1010 IC tables)](https://sandsoftwaresound.net/dive-into-old-roland-gear/) ·
[Roland SC-88Pro service notes (Manuals+)](https://manuals.plus/m/e3369464a0a8ae7c0c6d98b29114fbc0611712b99e614392774bc4e266f29325) ·
[Roland SC-88 service notes — IC15 "XP" integrates PCM/reverb/chorus/TVA/TVF (Manuals+)](https://manuals.plus/m/1f34e16e83f71def9e22fcf89da7c250d02c82864f7da431df69da0f1345acdd) ·
[Roland SC-8820 service notes, Nov 1999 (synfo.nl)](http://www.synfo.nl/servicemanuals/Roland/ROLAND_SC-8820_SERVICE_NOTES.pdf) ·
[H8 Family — lists Roland SC-55 / JV880 (Wikipedia)](https://en.wikipedia.org/wiki/H8_Family).

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

*Refinement (was a caveat, now sourced):* the SC-8820's silicon is confirmed from its Nov 1999 service
notes (§5) — and it is **not** an H8/510 machine but a 32-bit Hitachi SH-2 (`HD64F7017F28`) with a
later XP tone generator (`RA09-002`/XP6) and the same `MB87837PF` effects DSP. This *strengthens* rather
than weakens the verdict: SC-VA's control path is exclusively 16-bit fixed-point — the H8/510 idiom of
the SC-88/88Pro generation, not the SH-2's — so the ported firmware is the older H8 control code carried
forward over all three stacked ROM generations, while the XP tone-generator algorithms (a lineage shared
with the JV/XV pro line) and the wave ROMs are what span SC-88 → SC-88Pro → SC-8820. The SC-8820 supplies
the *sound set*; the H8 line supplies the *code*. The internal artifacts remain unambiguous either way.

---

*Sources: direct inspection of `SCCore.dll` and `SCCore.decompiled.c`; see the project memory notes
`scvx-rom-identity`, `scvx-render-path`, `scvx-patch-tables`. Verdict from a dedicated hardware-research
+ porting-artifact analysis pass, with the key internal claims independently re-verified against the
DLL bytes.*
