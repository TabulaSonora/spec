# SC-8820 wave ROM hardware & reference images

This document ties the 24 MB wave ROM embedded in `SCCore.dll` (see [PROVENANCE.md](PROVENANCE.md) §1)
to the **two physical mask ROM chips of a real SC-8820**, and documents the per-chip reference images
kept in `mine/`. Hardware facts are sourced from the
[Roland SC-8820 Service Notes, Nov 1999 (synfo.nl)](https://www.synfo.nl/servicemanuals/Roland/ROLAND_SC-8820_SERVICE_NOTES.pdf)
— parts list (p. 4) and block diagram (p. 10).

## 1. The physical chips

The SC-8820 stores its entire wave set on two mask ROMs on the main board:

| Location | Roland part code | Manufacturer part | Capacity | Contents (see §3) |
|---|---|---|---|---|
| **IC7**  | `01891445` | NEC `μPD23C128040LGY-823-MJH` | 128 Mbit (16 MB) | SC-88 `ver200` (8 MB) + first 8 MB of SC-88Pro `rom_make` |
| **IC39** | `02016156` | Macronix `MX23C6410RC-12` | 64 Mbit (8 MB) | last 4 MB of SC-88Pro `rom_make` + SC-8820 `8820_wv0` (4 MB) |

128 Mbit + 64 Mbit = 24 MB — exactly the size of the wave region embedded in `SCCore.dll`.
(The parts list marks IC39 with `#` = new/initial part for this model.)

Program code lives elsewhere entirely: the block diagram shows the SH7016 CPU bus carrying a 16 Mbit
program mask ROM (IC6) *or* 16 Mbit flash (IC5, exclusive selection), plus work DRAM (IC9) — the wave
ROMs are **not** on the CPU bus (see §4).

## 2. Reference images in `mine/`

| File | Size | SHA-256 |
|---|---|---|
| `roland-r01891445-upd23c128040lgy-823-mjh.ic7` | 16,777,216 | `34a8a0af36fe38cba9f442d17881cb96305ec905d5baa4cc819f10d8d4ec49f7` |
| `roland-r02016156-mx23c6410rc-12.ic39` | 8,388,608 | `7284bf21923f966c78019a5d697c8a11bb82ff3bf7a1af95957397feb8dfc597` |

(MD5, for cross-referencing ROM sets: `2ce0dfb99b0fbe4313b225d37d68ac95` / `35551cfb0cb36b95de6301a617249178`.)

**Origin — these are *not* physical chip reads.** They were produced by
`mine/roland-rom-splitter.c` from the pinned `SCCore.dll`
(SHA-256 `117E6AA1…C620BDB1`, see [DLL_LAYOUT.md](DLL_LAYOUT.md)):

| Image | DLL file-offset range | Note |
|---|---|---|
| `.ic7`  | `0x92700 … 0x1092700` | 16 MB read |
| (skip)  | `0x1092700 … 0x1092730` | 0x30 bytes of DLL-side (non-ROM) data — the "+0x30 drift" of PROVENANCE §1 |
| `.ic39` | `0x1092730 … 0x1892730` | 8 MB read |

Both images were re-verified byte-for-byte against the DLL (`cmp`) on 2026-08-01. The filenames record
the chips' service-notes identity (Roland part code, manufacturer marking, board location) so the
images serve as a reference to the actual device; a genuine programmer dump of IC7/IC39 has **not**
yet been compared (see §5 for what such a dump would settle).

To regenerate: compile `roland-rom-splitter.c` and run it in a directory containing the pinned
`SCCore.dll`.

## 3. Layout congruence — the DLL embeds the two chips back-to-back

The wave region's 1 MB block headers (PROVENANCE §1) map onto the chips as follows:

| Chip | Chip offset | Blocks | Generation label | Build date | Wave set |
|---|---|---|---|---|---|
| IC7  | `0x000000 … 0x800000` | 0–7   | `ver200`   | `1994-12-08` | SC-88 |
| IC7  | `0x800000 … 0x1000000`| 8–15  | `rom_make` | `1996-06-16` | SC-88Pro (first 8 MB) |
| IC39 | `0x000000 … 0x400000` | 0–3   | `rom_make` | `1996-06-16` | SC-88Pro (last 4 MB) |
| IC39 | `0x400000 … 0x800000` | 4–7   | `8820_wv0` | `1999-08-17` | SC-8820 |

Two structural observations:

- **The DLL's 0x30 discontinuity is the chip boundary.** The +0x30 header drift between wave blocks
  15 and 16 falls at exactly 16 MB into the region — precisely where the 128 Mbit IC7 ends and the
  64 Mbit IC39 begins. The DLL does not embed one 24 MB blob; it embeds **two chip images
  back-to-back with 0x30 bytes of host-side data between them**. The "bank A / bank B" split the
  engine uses ([DLL_LAYOUT.md](DLL_LAYOUT.md)) is the physical chip split.
- **The generation split does not align with the chip split.** The 12 MB SC-88Pro `rom_make` set
  straddles both chips (8 MB on IC7, 4 MB on IC39): the chips are a capacity partition, not a
  one-chip-per-generation layout.

Refined block-header detail (extends PROVENANCE §1): the first 6 magic bytes `A4 EB A5 2B E9 29` are
constant across all 24 blocks, but the remainder of the +0x00/+0x10 rows varies by generation
(`ver200` blocks: `… 0E C9 24 4B` / `69 2A AC C9 08 64 0C 0E A3 A6 …`; `rom_make`/`8820_wv0` blocks:
`… 6A EB A2 4B 0E C9` / `69 2A AC AE 08 A6 E9 08 A3 A6 …`, differing from each other only at +0x16),
with `0x08` as the pad byte. The +0x40 dwords `{0x4001, 0x20, 0x01}` are stored **little-endian** in
all three generations.

## 4. No CPU touches the wave ROMs

Per the service-notes block diagram, IC7 and IC39 connect **only to IC3 `RA09-002` (XP6), the
tone-generator ASIC** (labelled "Slave"), which also owns its own 4M delay-line DRAM (IC10). The main
CPU (IC1, SH-2 — block diagram labels it `SH7016 (64kB-MASK)`, the parts list `HD64F7017F28`) has on
its own bus only the program mask/flash ROM (IC6/IC5), work DRAM (IC9), the USB sub-CPU (IC2
`M37640E`), the effects DSP (IC4 `MB87837`, "LSP" in the parts list), and the XP6. Even the built-in
self test reads the wave ROM *through* the XP chip: the Device Test reports `Flash Rom / XP Chip /
Wave Rom / LSP Chip`, and the notes describe the wave ROM as tested via the sound generator chip.

This mirrors every earlier Sound Canvas generation (PROVENANCE §5: the wave ROMs always hung off the
XP ASIC), and it matters for interpreting byte order — next section.

## 5. Byte order: is the embedded image "byte-swapped"?

Short answer: **the question is ill-posed for these chips, and the DLL byte order is the canonical
one for this project.** Reasoning:

- A byte-order convention for a 16-bit-wide ROM only exists relative to some bus master's
  endianness. Here the only master is the XP6 ASIC (§4). Neither the big-endian SH-2 (SC-8820) nor
  the big-endian H8 (SC-88/88Pro) ever addressed these chips, so there is no CPU endianness for the
  wave data to be "in". How sample bytes map to D0–D15 words on the chip is a private contract
  between Roland's mask-data preparation and the XP6's wiring/fetch logic.
- PROVENANCE §4's evidence of a one-time big-endian→little-endian conversion concerns **CPU-visible
  firmware data** (tables, version strings). It does not imply the wave ROM was swapped for the
  port — and since the wave data was never CPU-visible, there was no *reason* to swap it. The
  plausible default is that SC-VA embeds the mask ROM byte stream as-is.
- The embedded image is internally byte-serial and self-consistent as-is: the ASCII generation
  labels and dates read correctly in stream order, the header dwords are little-endian (§3), and the
  DPCM sample streams (8-bit deltas, per-16-sample scale nibbles) decode correctly when consumed
  byte-serially — this is exactly what SC-VA's own decoder and this repo's reimplementation do.

What remains genuinely open is a *dump convention* question, not a data question: a programmer
reading the physical chips in 16-bit word mode could emit either byte-within-word order depending on
its serialization. Until someone reads a real IC7/IC39, the possible outcomes of a comparison are:

1. **Identical** to these images — no swap anywhere; the DLL provably embeds the mask ROM verbatim.
2. **16-bit byte-swapped** relative to these images — same data, differing only in the dumper's
   word-serialization convention (fix with a trivial swap).
3. Anything else — a real content difference (e.g. a later mask revision), which would be a finding.

Either of 1–2 would confirm the images here as faithful references to the physical chips. For this
project's purposes the DLL byte order is definitive regardless, since it is the order the (ported)
engine consumes.

---

*Sources: SC-8820 Service Notes (Nov 1999) parts list p. 4, block diagram p. 10, test-mode
description pp. 6–8; direct inspection of `SCCore.dll` (pinned build) and the `mine/` images,
2026-08-01. Related: [PROVENANCE.md](PROVENANCE.md) §§1, 4, 5; [DLL_LAYOUT.md](DLL_LAYOUT.md).*
