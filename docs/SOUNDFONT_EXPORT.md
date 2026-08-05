# Exporting the sound set as a SoundFont

A design for dumping the whole Sound Canvas sound set — every mapped tone, every drum kit, every
wave — into one SoundFont, with the five vintage maps (SC-55, SC-88, SC-88Pro, SC-8820, XG) layered
on top as `.sflist.json` remapping files rather than as five separate banks.

The target reader is [spessasynth_core_c](https://github.com/spessasus/spessasynth_core), whose C
port is what this design was measured against; file and line references below are to that tree.

## Honesty preamble (read this first)

**This is an approximation, and it will never match.** The goal is a bank that sounds good and is
recognisably the Sound Canvas — not a substitute for the engine. The gap is not an implementation
detail to be closed with more effort; it is structural, and §9 collects it in one place.

The short version: SoundFont 2 gives a voice **two** DAHDSR envelopes. This engine gives a partial
**three** envelopes of four segments each, plus a release, plus a per-segment shape flag, plus a
hold clock. Half the mapped library has an amplitude envelope that SF2 cannot spell at any level of
care. Everything downstream of the voice — the send effects, the insertion effects, the EQ, the
part-level control matrix — is outside the format entirely and is simply gone.

What the export *is* good for: a portable bank carrying the real sample data with the real key,
velocity and tuning mapping, playable anywhere, with envelopes fitted as closely as the format
allows. For anything that has to be right, render through the engine
([COMPARING_RENDERS.md](COMPARING_RENDERS.md)).

Measurements in this document were taken from `tables/` and the pinned `SCCore.dll`
([DLL_LAYOUT.md](DLL_LAYOUT.md)); each is reproducible from the table files named beside it.

## 1. Scale of the sound set

| | count | source |
|---|---|---|
| wave descriptors | 4,259 (3,703 unique ROM triples) | `tables/wavedesc_a.bin`, stride `0x16` |
| melodic tone records | 2,363, of which **1,694** are reachable from the five maps | `tables/tone_a.bin`, stride `0x100` |
| multisamples | 2,048, of which **798** are used by mapped tones | `tables/multisample_a.bin`, stride `0x8c` |
| drum kit records | **88** reachable, over 109 defined (row, program) slots | `g_drum_kits`, stride `0x50c` |
| sounding drum keys, all kits | 7,195, referencing **805** distinct melodic tones | receive plane `+0x480` bit 4 |
| preset slots across all five maps | 3,982 | `tables/lut1_2e30.bin` / `lut2_28b0.bin` / `lut3_32b0.bin` |
| alternate-articulation records | 50 | `tables/layered_1896690.bin`, stride `0x18` |

Per map, from the three-level lookup ([FINDINGS.md](FINDINGS.md), "The static patch directory"):

| Map | selector | banks | preset slots | distinct tones |
|---|---|---|---|---|
| SC-55 | 1 | 15 | 418 | 354 |
| SC-88 | 2 | 24 | 610 | 575 |
| SC-88Pro | 3 | 45 | 966 | 932 |
| SC-8820 | 4 | 51 | 1,454 | 1,420 |
| XG | 0x77 | 45 | 534 | 450 |

The union is 1,694 melodic tones and 50 alternate-articulation entries. **The five maps overlap
heavily** — 3,982 preset slots resolve to 1,746 distinct tone references — which is the whole reason
one shared bank plus five remapping files is worth doing rather than five banks.

### Sample data

Summing every descriptor's extent gives 31.8 M samples; deduplicating by exact `(region, loop,
start)` triple gives 25.6 M; merging overlapping extents per region gives **24.3 M samples**, or
**46.4 MB as 16-bit PCM**. The three numbers differ because descriptors share and nest — one wave's
range is frequently a subrange of another's. A per-descriptor dump wastes about 30%; dedup by merged
ROM extent, not by descriptor.

24.3 M of the ROM's 25.1 M sample positions are referenced, so the export covers essentially the
whole 24 MB ROM ([HARDWARE_ROMS.md](HARDWARE_ROMS.md)).

The native rate is **32 kHz** — the control tick is 100 Hz over 320-sample blocks.

## 2. Container: the index widths decide it

Not the codec. The `igen` index width.

Drum kits force the object layout. A kit preset needs one preset zone per sounding key — up to 128,
or 256 where a key's tone has two partials — and each zone must point at an instrument. So
instruments have to be **partial-level**, with the envelope and filter generators sitting in the
instrument's global zone where 128 drum keys can reference them cheaply. Push the partial parameters
up into preset zones instead and every kit re-emits ~30 generators × 256 zones.

With instrument-per-partial over the 1,694 mapped melodic tones:

```
inst = 2,644     ibag = 25,413     igen ≈ 271,000
```

SF2's `ibag` and `igen` indices are `uint16`. **271,000 does not fit.**

The alternative — instrument per multisample (798 instruments, 5,560 key zones, 6,358 `ibag`, about
61,000 `igen`) — squeezes under the ceiling only by moving the partial parameters into preset zones,
at which point `pgen` overflows instead: roughly 79,000 for the melodic half alone, before a single
drum kit. There is no layout that fits.

**So `xdta` is mandatory, not an optimisation.** It is the SF2.04 extension chunk carrying the high
words of every bag and generator index, and spessasynth reads it: `soundfont_reader.c:318-342`
collects `xdta`'s sub-chunks and merges the high words when the counts match the base chunks. The
same reader accepts `sfen` as a form type and handles `RIFS`/RIFF64 64-bit chunk sizes.

Design decisions that follow:

- **Write `xdta` from the first commit.** Do not build a `uint16` writer and retrofit; the counts are
  4× over from the start, so a `uint16` writer can never emit a usable file.
- **Instrument per partial**, preset per tone and per drum kit. The 4× zone cost against the
  multisample-deduped layout is paid back by the drum kits.
- `RIFF64` is not needed. 46 MB of PCM, or ~30 MB compressed, is nowhere near 4 GB.

One note for anyone extending this: the `ISFe` LIST **is parsed into a chunk handle and then never
read** — opened at `soundfont_reader.c:241`, closed at `:811`/`:849`, no field ever consulted. The
SFe metadata block is unoccupied space in this reader today, which is where a codec declaration
would have to go.

## 3. Sample codec: do not carry the Roland ADPCM

The tempting idea is to declare an SFe extension and ship the wave ROM's own delta+exponent streams
verbatim. Three reasons not to, in order of weight:

**It does not win on size.** The codec is one signed delta byte per sample plus a 4-bit shift
exponent per 16 samples — 10 bits per sample, so 24.3 M samples ≈ **29 MB**. FLAC over 32 kHz mono
instrument material lands around 55–65% of 16-bit, i.e. **26–30 MB**. The bespoke format's best case
is parity with an off-the-shelf lossless codec that any conforming reader already handles.

**It has no random access.** The predictor has no leak and integrates from the data start; the
preamble deltas between the 32-sample exponent-block boundary and the data start ride under the
entire wave as a DC constant (measured at −0.041015625 on `Crash Cym.1` — see FINDINGS, "Hearing the
sauce"). Any decoder must run each wave from its true start regardless. That is not a speed
objection — decoding is lazy and cached per sample either way, so it happens once — but it does mean
the bespoke codec has no access advantage to weigh against its cost.

**It buys a format nobody else reads.** It means defining a sample type in `ISFe`, patching the
magic-byte dispatch in `soundbank.c`, and producing a file that is a SoundFont only by extension.

### What to write instead: a three-way switch

The exporter should carry a codec switch and support all three, because they trade differently and
no one of them is right for every use:

| `--codec` | Container | Size | Fidelity | Read by |
|---|---|---|---|---|
| `pcm` | plain SF2, `smpl` (+ optional `sm24`) | ~46 MB | exact | everything |
| `flac` | SF3-style, per-sample `fLaC` chunks | ~26–30 MB | lossless | **spessasynth only** |
| `vorbis` | SF3, per-sample `OggS` chunks | ~12–18 MB | lossy | spessasynth, FluidSynth, most SF3 readers |

`soundbank.c:468-480` dispatches each sample's compressed slice on magic bytes — `OggS` → Vorbis,
`fLaC` → FLAC — so both compressed forms need no engine change beyond setting the `0x10` compression
flag in `sampleType`. Only the codec identity and the encoder differ; the rest of the writer is
shared.

**`pcm` is the reference output.** Build and verify against it first — it keeps the writer testable
against a byte-exact decode, and 46 MB is not a problem locally. Compression is a post-pass over an
already-correct bank.

**`flac` is the default for local use.** Lossless against the decode, at the same size the original
ADPCM would have been, which is the result that makes carrying the Roland codec pointless.

**`vorbis` is the default for anything shared.** Canonical SF3 is Vorbis-only, so a Vorbis bank
loads in FluidSynth and a FLAC one does not. Per-sample FLAC in SF3 is a spessasynth extension and
nothing else implements it. The cost is that the ROM's material includes short percussive transients
— the crashes, the closed hats, the noise-based effects — which is where Vorbis at bank-sized
bitrates is least transparent.

**sf2pack** is the fourth possibility and is not worth supporting. It puts one global stream in
`smpl` and addresses samples by absolute offset into it (`soundfont_reader.c:487-500`, comment
`/* Ugh! Absolute sample counts! */`). That addressing is not the problem it looks like — spessasynth
decodes lazily and caches per sample, so a seek happens once, on a sample's first use, and is fast in
both FLAC and Vorbis. The reason to skip it is that it buys nothing over per-sample chunks while
being a less widely read container.

## 4. Bank layout and the five maps

### Bank addressing

`soundfont_reader.c:701-708` splits the 16-bit `wBank` field of `phdr`:

```
bank_msb      = wBank & 0x7f
is_gm_gs_drum = wBank & 0x80
bank_lsb      = wBank >> 8
```

So a single file addresses 128 MSB × 128 LSB × 128 programs plus a drum flag — 2,097,152 melodic
slots against the 1,694 needed. `sflist`'s `"bank"` value is that same packed pair, which
`ss_filtered_bank_build_one` compares as `p->bank_msb | (p->bank_lsb << 8) | (is_gm_gs_drum ? 128 :
0)` (`soundbank.c:885`).

This is a spessasynth convention, not SF2 — the specification defines bank 0–127 plus 128 for
percussion, and nothing more. A reader that follows the specification will see the `wBank >> 8`
pages as bank 0 and collapse all 19 of them onto each other.

ROM-aligned layout, chosen so a preset number is a stable name for a tone across all five maps:

| Object | `wBank` | `program` | range used |
|---|---|---|---|
| melodic tone *N* | `(N >> 7) << 8` | `N & 0x7f` | LSB pages 0–18, MSB 0 |
| drum kit *K* | `0x80 \| ((K >> 7) << 8)` | `K & 0x7f` | LSB page 0, drum flag set |

Tone numbers are the ROM's own, so the bank is self-describing against `tables/tone_a.bin` and a
regenerated bank keeps its numbering when unrelated tones change.

### The five maps

One `.sflist.json` per map, each with a `patchMappings` array whose `source` is the tone's stable
slot above and whose `destination` is the vintage's real (bank, program). 418 to 1,454 entries each,
a few hundred kilobytes of JSON in total against a ~30 MB bank — which is the storage win the design
exists for.

`docs/generators/tg300b-sflist/tg300b_map_generator.c` in the spessasynth tree is a working
precedent for generating exactly this shape.

Two things the generator has to handle that `sflist` will not do for it:

- **The GS capital-tone fallback.** When a selected variation bank has no entry for a program, the
  module sounds the bank-0 capital tone rather than falling silent (the `lut3_resolved` rule). One
  honky-tonk part in `passport.mid` selects bank 5, whose program-3 slot is empty, and every note
  would otherwise drop. `sflist` has no fallback rule, so every fallback must be emitted as an
  explicit mapping. This inflates the entry counts above towards the full 128 × banks grid; budget
  for it.
- **Drum rows.** SC-55 selects drum map row 3, SC-88 row 2, SC-88Pro row 1, SC-8820 row 0, XG row 4;
  row 5 is GM2's. The kit a program resolves to differs per row, so each map's file needs its own
  drum mappings, not a shared block.

Alternate-articulation entries (50 records) are **not** exportable. The second reference in each is
reachable only through the mono/solo path and an inter-note timing test, which has no expression in
SF2. Export the primary reference and drop the alternate.

## 5. Envelope translation

This is where the effort belongs and where the loss is.

The engine gives each partial three envelopes — TVA, TVF and pitch — each with four segments, a
release, and per-segment rate scaling from key-follow and velocity, plus a hold clock at note-on.
SF2 gives a voice two DAHDSRs: `volEnv` (delay, attack, hold, decay, sustain, release) and `modEnv`,
the latter reaching pitch and filter cutoff through `modEnvToPitch` and `modEnvToFilterFc`.

Measured across the 2,644 partials of the 1,694 mapped tones:

| | count | share |
|---|---|---|
| pitch envelope active | 454 | 17.2% |
| filter envelope active | 2,142 | 81.0% |
| **both active** — one `modEnv` asked to do two jobs | **373** | **14.1%** |
| TVA fits attack + decay + sustain (≤ 2 moving segments) | 1,325 | **50.1%** |
| TVA with 3 moving segments | 863 | 32.6% |
| TVA with 4 moving segments | 456 | 17.2% |
| TVA with at least one linear-shape segment | 1,671 | 63.2% |

("Active" for pitch means a non-zero depth at block `0x18`/`0x19` with a non-neutral target among
`0x1b`–`0x1e`; for the filter, a non-zero depth at `0x33` with targets at `0x3a`–`0x3e` that are not
all equal. Moving TVA segments are counted from the level bytes at `0x5a`–`0x5d` against a note-on
level of zero; the shape flag is bit 7 of the rate bytes at `0x5e`–`0x61`.)

### The amplitude envelope is the bigger loss

**Half the mapped library has a 3- or 4-stage amplitude envelope**, and SF2's `volEnv` has exactly
two moving stages. There is no escape hatch in the format: `modEnv` reaches only pitch and filter
(`dls_reader.c:359-360`), and there is no `modEnvToVolume` generator. Adding one — generator 67 in
spessasynth's extended space — would fix it for spessasynth alone and produce a bank no other reader
plays correctly.

The per-segment shape flag compounds it. SF2 fixes attack as linear in amplitude and decay/release
as linear in centibels; the engine picks per segment between linear and a fast-approach curve, and
63% of partials mix the two within one envelope. So even the partials that *do* fit in two stages
have the wrong curvature on at least one of them.

The fit is therefore a genuine approximation problem, and worth treating as its own measurable
stage rather than as a formula: pick the two segments to model by largest level excursion weighted
by duration, fold the remainder into the sustain level, and A/B the result. Document the selection
rule in the exporter so a change to it is visible in a diff.

### The pitch/filter collision is survivable

The 14.1% needing both envelopes from one `modEnv` are less bad than the number suggests. Pitch
envelopes here are usually short attack transients while filter envelopes carry the sustained shape,
so fitting `modEnv` to the filter and scaling the pitch contribution through `modEnvToPitch` keeps
the pitch gesture's direction and rough magnitude and loses its independent timing.

Where a partial's pitch envelope is the dominant gesture — the `.o` variation tones, whose one-shot
mode makes the pitch envelope the whole articulation — the priority should invert and `modEnv`
should follow the pitch.

### What does translate

| Engine | SF2 | Notes |
|---|---|---|
| multisample key zones | instrument key ranges | direct, after the `0x40 − key_center` shift back to MIDI notes |
| velocity window | `velRange` | hard split only; see §9 |
| root key, both fine tunes | `overridingRootKey`, `fineTune`, `coarseTune` | the effective root is `root_key × 1000 + 1024 − fine − (second_fine − 1024)` |
| four-attenuation level chain | `initialAttenuation` | partial level, velocity-crossfaded level, zone level, tone master level all collapse into one static value |
| per-partial pan | `pan` | direct |
| envelope rate key-follow | `keynumToVolEnvDecay`, `keynumToVolEnvHold` | approximate — the engine's key-follow is a table, SF2's is a linear slope |
| LFO1 (tone-common) | `vibLFO` | LFO1 is the one that takes the part's vibrato modifiers, so it is the vibrato LFO |
| LFO2 (per-partial) | `modLFO` | |
| LFO delay and fade-in | `delayVibLFO` / `delayModLFO` | delay maps; the fade-in ramp does not, SF2 has no equivalent |
| drum kit level, pan, coarse pitch | preset-zone generators | the coarse-pitch plane supplies the key, pivoting on 60 |
| drum mute groups | `exclusiveClass` | direct |

**Both LFOs get all three destinations**, which standard SF2 does not allow — `vibLFO` is
pitch-only there. spessasynth's extended generators 63 and 64
(`SS_GEN_VIB_LFO_AMPLITUDE_DEPTH`, `SS_GEN_VIB_LFO_TO_FILTER_FC`) supply the missing routes, and
they are honoured from a file: `soundbank.c:684` bounds-checks generator types against
`SS_GEN_COUNT` = 67, not 60. Another reader will drop them, leaving LFO1 as pitch-only vibrato.

## 6. Modulators

The default modulator set is replaceable wholesale. `soundfont_reader.c:223-232` reads a `DMOD`
chunk from `INFO` and sets `custom_default_modulators`; `soundbank.c:657-661` then substitutes it
for the built-in `SS_DEFAULT_MODULATORS` (17 entries, `soundbank.c:132-292`) on every preset from
that bank.

So the approach is: transcribe the built-in 17 as the baseline, then retune and extend for GS. What
differs, from the engine's own behaviour:

| Controller | Built-in default | GS behaviour | Where it belongs |
|---|---|---|---|
| CC64 half-damper | none | release rate scaled by roughly `1 − v/128`, but **only on the 57 piano tones** (tone header `0x0d` bit 2) | per-instrument `imod`, not `DMOD` — every other tone quantises the pedal to 0 or 0x7f |
| CC72/73/75 envelope modify | → `volEnv` release/attack/decay | also moves the **filter** envelope, and only on partials with bit 4 of block byte `0x0e` set | the `volEnv` half in `DMOD`; the `modEnv` half as `imod` on opted-in partials |
| CC71 resonance | → `initialFilterQ`, amount 250 | own curve; the engine's damping is reciprocal-Q, so 0x40 is exactly 1.0 and smaller is more resonant | `DMOD`, fitted amount, sign checked |
| CC74 brightness | → `initialFilterFc`, 9600 cents | own warp and a resonance-dependent ceiling | `DMOD`, fitted amount |
| CC76/77/78 vibrato delay/rate/depth | **none** | bias LFO1's table indices | `DMOD`, to `delayVibLFO` and the extended `SS_GEN_VIB_LFO_RATE` (62) |
| mod wheel | → `vibLfoToPitch`, 50 cents | default depth `0x0a` through the cents table, total clamped to ±6000 milli-semitones | `DMOD`, fitted amount |
| velocity → attenuation | one concave curve, 960 cB | **one of sixteen** curves, selected per partial (block `0x2e` low nibble) | not representable; see §9 |

Two structural notes. First, an SF2 modulator's curve is one of four shapes (linear, concave, convex,
switch), so a sixteen-row table is approximated by whichever of four fits least badly — and it is
per-partial, so it cannot live in `DMOD` at all. Second, `DMOD` replaces the entire default set, so
anything from the built-in 17 that is still wanted must be copied across; a `DMOD` chunk holding only
the additions silently removes velocity-to-attenuation and the rest.

## 7. Loop modes and sample baking

SF2's `sampleModes` offers no-loop, loop, and loop-until-release. The ROM has more, from the
descriptor flag byte at `+0x0a`:

| Mode | flag | count | share | treatment |
|---|---|---|---|---|
| forward | 0 | 2,757 | 64.7% | direct |
| ping-pong | bit 0 | 612 | 14.4% | bake: mirror the loop region into a forward loop |
| reverse | bit 2 | 218 | 5.1% | bake: reverse the data, remap the loop points |
| flag bit 1 set | 2 | 672 | 15.8% | takes no part in the sampler dispatch; ignore |

No descriptor sets both bit 0 and bit 2, so the two bakes are independent. 876 descriptors have the
loop point at or past the physical end and are effectively one-shot.

Baking ping-pong costs one extra loop length of sample data per affected wave. It is exact for a
loop played an even number of times and drifts by at most one traversal at note-off, which is
inaudible against the release.

Other baking the exporter must do:

- **Dedup by merged ROM extent** before emitting samples, per §1.
- **The 46-sample inter-sample gap** the specification requires: 3,870 samples × 46 ≈ 356 KB.
  Negligible.
- **The preamble DC term.** Decoding must start at the descriptor's `loop` exactly and index the
  exponents by absolute sample position; rounding the start down to a 32-sample block boundary
  begins the integration up to 31 samples early and displaces the whole wave for its entire length,
  because the predictor has no leak and nothing downstream blocks DC.

## 8. Suggested build order

1. **Sample extractor.** Dedup by merged extent, decode through the wave-ROM reader, bake ping-pong
   and reverse, emit a sample table plus an extent → sample-index map. Self-contained and verifiable
   against a single-note render.
2. **Writer, with `xdta` from the first commit.** Instrument per partial, preset per tone and per
   kit, ROM-aligned bank numbering, `--codec pcm`.
3. **Envelope fitting as its own stage**, with a documented selection rule and an A/B harness
   rendering the same note through the engine and through spessasynth. §5 says this is where the
   quality lives.
4. **`DMOD` and the per-instrument modulators.**
5. **The five `sflist` generators**, including the capital-tone fallback expansion and per-map drum
   rows.
6. **The `flac` and `vorbis` codec paths**, once the PCM bank is byte-verified. They share the whole
   writer and differ only in the encoder and the magic bytes, so they are cheap once step 2 is
   correct.

## 9. The limitations, collected

Everything above that costs fidelity, in one list. This is the section to read before deciding what
the export is for.

### Structural — no amount of care fixes these

1. **The amplitude envelope has two stages instead of four.** 49.9% of mapped partials do not fit.
   No SF2 mechanism recovers the missing stages, and `modEnv` cannot reach volume.
2. **The per-segment shape flag has no equivalent.** SF2 fixes attack as linear-in-amplitude and
   decay/release as linear-in-centibels; 63.2% of partials mix linear and fast-approach segments
   within one envelope.
3. **Pitch and filter share one `modEnv`.** 14.1% of partials use both, and their timings are then
   forced together.
4. **Velocity crossfade between partials is a hard split.** The engine crossfades a partial's level
   between its window edges through a selected curve; `velRange` is a boundary. Approximating it
   costs 3–4 velocity bands per partial and multiplies every zone count in §2.
5. **Sixteen velocity response curves become one of four modulator shapes**, and being per-partial
   they cannot be centralised in `DMOD`.
6. **The hold clock does not survive.** `delayVolEnv` covers the delayed-start case; the one-shot
   form (`0xff`, the `.o` variation tones — held for the voice's whole life, with note-off taking a
   fast fade rather than the release) has no expression.
7. **Alternate articulations are dropped.** 50 records whose second reference needs mono/solo mode
   and an inter-note timing test.
8. **The LFO fade-in ramp is lost.** SF2 has LFO delay but no fade.
9. **Random LFO shapes are lost.** Waveform selectors 1–3 redraw on phase wrap from the engine's
   shared noise generator; SF2 LFOs are periodic functions of phase.
10. **Pitch start jitter is lost.** A per-note-on random draw, with a deliberately asymmetric range.
11. **Everything after the voice is gone.** Send reverb, chorus and delay; the 61-plus insertion
    effects; the EQ; the part-level control matrix. SF2 carries reverb and chorus *send levels* and
    nothing that generates them.
12. **Per-key drum reverb, chorus and delay depths** (kit planes `0x300`/`0x380`/`0x400`) have no
    per-zone equivalent beyond the two send generators.
13. **The engine's filter is a state-variable filter with four taps** — lowpass, highpass, bandpass
    and a sixth type — selected per partial. SF2 has one lowpass. Highpass and bandpass partials
    will be wrong, not approximate.
14. **`Rx.Note On` / `Rx.Note Off` per drum key** are engine state that SysEx can rewrite; the
    export freezes the ROM defaults.

### Reader-dependent — the bank works, but only somewhere

15. **`xdta` is required**, so a reader without SF2.04 extension support cannot load the file at all.
16. **`wBank >> 8` as a bank LSB is a spessasynth convention.** A specification-following reader sees
    19 pages collapsed onto bank 0.
17. **Extended generators 61–66** (both LFOs' amplitude and filter routes) are spessasynth-only.
    Elsewhere LFO1 degrades to pitch-only vibrato.
18. **Per-sample FLAC chunks are a spessasynth extension.** Canonical SF3 is Vorbis-only, so
    `--codec flac` produces a bank only spessasynth reads. `--codec vorbis` is portable but lossy,
    and `--codec pcm` is portable and exact at 46 MB; §3 has the trade.

### Deliberate approximations

19. Ping-pong loops are baked to forward loops; reverse waves are baked reversed.
20. The four-stage attenuation chain is collapsed into one static `initialAttenuation`, so the
    velocity-dependent part of it is frozen at the fitting velocity.
21. Envelope rate key-follow becomes a linear slope, from a table.
22. The GS capital-tone fallback is expanded into explicit `sflist` entries rather than being a rule,
    so a bank regenerated with different coverage needs regenerated maps.

## 10. Distribution

The output carries Roland's wave ROM data in a different container. Decoding it to PCM or
recompressing it to FLAC does not change what it is, so a generated bank sits on the same side of the
line as the per-chip reference images described in [HARDWARE_ROMS.md](HARDWARE_ROMS.md) §2: it is
built locally by someone who has the DLL, and it is not committed or published. The exporter itself —
code, tables, this document — is publishable.
