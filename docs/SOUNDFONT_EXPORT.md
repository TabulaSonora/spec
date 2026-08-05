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

### Sample data `[measured on the built export]`

**64.8 MB as 16-bit PCM**, in 3,880 sample runs. That is what the exporter actually produces, and
it is half again the 46.4 MB an earlier version of this section projected. Two of that estimate's
assumptions were wrong, and both were wrong in the same direction:

| Reading | Samples | 16-bit MB |
|---|---|---|
| every descriptor's extent, summed | 31.8 M | 60.7 |
| deduplicated by exact `(region, loop, start)` | 25.6 M | 48.9 |
| overlapping extents merged per region | 24.3 M | 46.4 |
| **what the export stores** | **34.0 M** | **64.8** |

**Merging overlapping extents is not available**, which is what §3 records: the codec integrates
from zero at the exponent-block boundary below each wave's own data start, so two waves over
overlapping ROM have identical *shape* and levels differing by a DC constant — up to 0.217 on a ±1
signal, and SF2 has no generator that can add one back. Waves are therefore keyed on their exact
extent. Sharing still happens where the constant is zero, which is 379 of the 3,880 runs.

**Ping-pong costs extra sample data**, because the baked round trip appends the descending leg to
the wave (§7). That is 549 runs carrying about 15.2 M samples between them, against roughly 7.5 M
if they were stored as plain forward loops.

Broken out by bake, over the census the exporter actually reaches:

| Bake | Runs | Samples | 16-bit MB |
|---|---|---|---|
| forward loop | 2,639 | 12.3 M | 23.4 |
| ping-pong | 549 | 15.2 M | 29.0 |
| one-shot | 512 | 4.3 M | 8.3 |
| reverse | 180 | 2.0 M | 3.9 |

**The census reaches all 4,259 descriptors**, not the 3,870 an earlier melodic-only count
suggested — the drum kits pull in the remainder. So the export covers essentially the whole 24 MB
ROM ([HARDWARE_ROMS.md](HARDWARE_ROMS.md)).

Add the specification's 46-sample gaps and the finished PCM is 64.8 MB; the written SF2 with all
generators and modulators is **69.3 MB**, or **37.3 MB** as FLAC and **30.0 MB** as Ogg Vorbis
(§3 has the measured table).

The native rate is **32 kHz** — the control tick is 100 Hz over 320-sample blocks.

## 2. Container: the index widths decide it

Not the codec. The `igen` index width.

Drum kits force the object layout. A kit preset needs one preset zone per sounding key — up to 128,
or 256 where a key's tone has two partials — and each zone must point at an instrument. So
instruments have to be **partial-level**, with the envelope and filter generators sitting in the
instrument's global zone where 128 drum keys can reference them cheaply. Push the partial parameters
up into preset zones instead and every kit re-emits ~30 generators × 256 zones.

With instrument-per-partial over the 1,694 mapped melodic tones, the projection was:

```
inst = 2,644     ibag = 25,413     igen ≈ 271,000
```

**The built export measures higher still** — 3,387 instruments, 23,950 `ibag` and **330,003 `igen`**
— because the census reaches every descriptor rather than the melodic subset, and because each zone
carries a fitted volume envelope and a modulation envelope on top of its mapping.

SF2's `ibag` and `igen` indices are `uint16`. **330,003 does not fit**, and neither did the
projection.

The alternative — instrument per multisample (798 instruments, 5,560 key zones, 6,358 `ibag`, about
61,000 `igen`) — squeezes under the ceiling only by moving the partial parameters into preset zones,
at which point `pgen` overflows instead: roughly 79,000 for the melodic half alone, before a single
drum kit. There is no layout that fits.

**So `xdta` is mandatory, not an optimisation.** It is the SF2.04 extension chunk carrying the high
words of every bag and generator index, and spessasynth reads it: `soundfont_reader.c:318-342`
collects `xdta`'s sub-chunks and merges the high words when the counts match the base chunks. The
same reader accepts `sfen` as a form type and handles `RIFS`/RIFF64 64-bit chunk sizes.

Design decisions that follow:

- **Write `xdta` from the first commit**, and for every bank however small. Do not build a `uint16`
  writer and retrofit; the counts are 5× over, so a `uint16` writer can never emit a usable file.
  The negative control is worth knowing: the same bank written without the `xdta` LIST does not
  degrade, it makes the reader **segfault**, because the truncated indices address nothing.
- **Instrument per partial**, preset per tone and per drum kit. The 4× zone cost against the
  multisample-deduped layout is paid back by the drum kits.
- `RIFF64` is not needed. 64.8 MB of PCM, or ~40 MB compressed, is nowhere near 4 GB.

One note for anyone extending this: the `ISFe` LIST **is parsed into a chunk handle and then never
read** — opened at `soundfont_reader.c:241`, closed at `:811`/`:849`, no field ever consulted. The
SFe metadata block is unoccupied space in this reader today, which is where a codec declaration
would have to go.

## 3. Sample codec: do not carry the Roland ADPCM

The tempting idea is to declare an SFe extension and ship the wave ROM's own delta+exponent streams
verbatim. Three reasons not to, in order of weight:

**It wins on size, and that is not enough.** *(Corrected: an earlier version of this section claimed
it did not win. That was wrong, and wrong in the bespoke format's favour to admit.)* The codec is
one signed delta byte per sample plus a 4-bit shift exponent per 16 samples — 10 bits per sample,
so the ROM's 24.3 M referenced positions come to about **29 MB**. FLAC over the *baked* PCM measures
**33.0 MB**, because SF2 has no ping-pong and the bake appends a descending leg to every one of the
549 ping-pong runs (§1). Carrying the codec means carrying the traversal too, and a delta-domain
traversal costs no extra bytes at all — so the measured gap is about **14%**, in the ROM format's
favour.

It is still not worth it, on the two reasons below. But the trade is a real one and the size column
is not the argument against it.

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
`[measured]` — sample data, then the finished file with all generators and maps:

| `--codec` | Container | Sample data | File | Fidelity | Read by |
|---|---|---|---|---|---|
| `pcm` | plain SF2, `smpl` | 64.8 MB | **69.3 MB** | exact | everything |
| `flac` | SF3-style, per-sample `fLaC` chunks | 33.0 MB | **37.3 MB** | **bit-exact** | **spessasynth only** |
| `vorbis` | SF3, per-sample `OggS` chunks | 25.7 MB | **30.0 MB** | rms 0.0049 | spessasynth, FluidSynth, most SF3 readers |

FLAC comes out at **51% of PCM**, better than the 55–65% estimated above and better than the
36–42 MB §1 projected from it. Encoding the bank takes about 13 seconds, Vorbis about 20.

"Bit-exact" is measured, not assumed: decoding both banks back through the reader and comparing
gives a worst difference of 0.000000 over 678,071 compared points. It did not start that way — the
first measurement showed exactly one 16-bit LSB, which was not the codec but the writer's
float-to-integer conversion *truncating* where the encoder's *rounded*. Both round now.

`soundbank.c:468-480` dispatches each sample's compressed slice on magic bytes — `OggS` → Vorbis,
`fLaC` → FLAC — so both compressed forms need no engine change beyond setting the `0x10` compression
flag in `sampleType`. Only the codec identity and the encoder differ; the rest of the writer is
shared.

**`pcm` is the reference output.** Build and verify against it first — it keeps the writer testable
against a byte-exact decode, and 64.8 MB is not a problem locally. Compression is a post-pass over an
already-correct bank.

**`flac` is the default for local use.** Lossless against the decode, and about 14% larger than the
original ADPCM's ~29 MB — a real cost, paid for a file that unmodified software can read.

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
slot above and whose `destination` is the vintage's real (bank, program).

**`source` selects presets inside the bank file and `destination` is where MIDI addresses them** —
the opposite of what the names suggest at a glance, and it is `ss_filtered_bank_build_one`
(`soundbank.c:881-906`) that fixes the direction.

`[measured]` The generated files run **1,930 to 6,566 mappings each, 4.4 MB of JSON across all
five** — an order of magnitude more than an earlier version of this section guessed, because the
capital-tone fallback below is most of every file. That is still a rounding error against a 68 MB
bank, so the storage argument holds; it is the *file count* claim that was wrong, not the
conclusion.

| Map | banks | melodic mappings | of which fallback | drum |
|---|---|---|---|---|
| SC-55 | 15 | 1,920 | 1,502 | 10 |
| SC-88 | 24 | 3,072 | 2,462 | 15 |
| SC-88Pro | 45 | 5,760 | 4,794 | 26 |
| SC-8820 | 51 | 6,528 | 5,074 | 38 |
| XG | 45 | 5,888 | 5,226 | 11 |

The non-fallback counts — 418, 610, 966, 1,454 — are exactly the per-map preset slots in §1, which
is the check that the bank iteration matches the lookup tables.

**The two bank layouts are not the same shape**, and getting it backwards produces five files that
all load, all validate, and quietly answer nothing for every variation a file selects:

- **GS** carries the variation in the bank **MSB**, and selects the vintage itself with bank LSB
  1–4. Choosing the vintage is what five separate lists are *for*, so destinations sit at LSB 0 and
  let the MSB carry the variation as an ordinary GS file expects.
- **XG** carries the variation in the bank **LSB**. The MSB names a column instead: MSB 64 is the
  SFX voice bank, which the module reaches by substituting lookup bank `0x7d` whatever the LSB says,
  so it is *one* destination rather than 128.

Measured discrimination: MSB 8 program 0 answers `Piano 1w` under SC-55 and nothing under XG, LSB 8
does the reverse, and MSB 64 program 90 answers `Submarine` rather than the `Polysynth` it is at
LSB 0.

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
  drum mappings, not a shared block. The kit *names* are a free check that the rows are right, since
  every GS kit is upper case and every XG kit lower: SC-55 answers `STANDARD`/`ROOM`/`TR-808` where
  XG answers `standard kit`/`room kit`/`analog kit`. Drum presets keep their percussion flag across
  the remap — the rule copies `is_gm_gs_drum` and rewrites only the two bank bytes — so a drum
  destination needs no flag of its own.

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
| CC64 half-damper | none | release rate scaled by roughly `1 − v/128`, but **only on the 57 piano tones** (tone header `0x0d` bit 2) | per-instrument `imod`, not `DMOD` — see below |
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

### Per-instrument modulators `[implemented]`

What cannot live in `DMOD` lives in each instrument's **global zone**, and the merge rule is what
makes that work: the reader takes the instrument zone's modulators, then *unique* global-zone ones,
then *unique* bank defaults (`ss_preset_get_synthesis_data`). A global modulator whose source and
destination match a default therefore **replaces** it rather than stacking with it.

| What | Where it goes | Population |
|---|---|---|
| velocity → `initialAttenuation`, at the partial's own span | every instrument | 3,387 |
| CC#64 → `releaseVolEnv` (half-damper) | the 57 piano tones' instruments | 104 |
| CC#72/73/75 → `modEnv` attack/decay/release | partials with bit 4 of block `0x0e` | 2,203 |

The velocity one is the substantive change. The default set applies a uniform 960 cB concave
response to every voice; the engine crossfades between a partial's own two edge levels across its
own velocity window, and those spans measure **0 to 868 cB** across the library. `Piano 1` is a fair
illustration — its first partial wants 80 cB and its second 360, where the default gave both 960.
It remains an approximation, because the modulator's curve spans the whole 0–127 range while the
partial's crossfade spans only its window, so a narrow window is under-served.

**Half-damper must not be a bank default.** Only the 57 piano tones respond to a partly-pressed
pedal; every other tone quantises CC#64 to fully up or fully down before it reaches the release
ramp, so a bank-wide CC#64 → release lengthens the whole library's tails.

One negative result, recorded because a zero here looks like a bug. The velocity crossfade *can*
run backwards — `level_at_high` below `level_at_low`, a partial fading in from the top of its
window — and both the engine and the exporter handle it. **No mapped partial uses it:** of 2,644,
2,639 rise with velocity, 5 are flat, none invert, and no velocity window is stored reversed
either. The direction handling is defensive rather than load-bearing.

## 7. Loop modes and sample baking

SF2's `sampleModes` offers no-loop, loop, and loop-until-release. The ROM has more, from the
descriptor flag byte at `+0x0a`:

| Mode | flag | count | share | treatment |
|---|---|---|---|---|
| forward | 0 | 2,757 | 64.7% | direct |
| ping-pong | bit 0 | 612 | 14.4% | bake one round trip of the real traversal into a forward loop |
| reverse | bit 2 | 218 | 5.1% | bake: reverse the data, remap the loop points |
| flag bit 1 set | 2 | 672 | 15.8% | takes no part in the sampler dispatch; ignore |

No descriptor sets both bit 0 and bit 2, so the two bakes are independent. 876 descriptors have the
loop point at or past the physical end and are effectively one-shot.

### Ping-pong bakes exactly, but not by mirroring `[measured]`

**Do not mirror the decoded PCM.** The traversal loops in the *delta* domain: `WaveReader::generate`
in NativeTS walks the index up to `data_end`, back down to `loop_start` and up again, while the
predictor keeps **adding** deltas on every leg rather than subtracting them on the way back. The
descending leg is therefore the wave inverted and time-reversed, which is not its mirror. Measured
over all 612 ping-pong waves, the descending leg matches the forward samples read backwards on
**none** of them, with a worst-case error around 0.47 on a ±1 signal.

What it *is* is periodic. A round trip re-applies the delta at each turnaround — the index is
unchanged when the leg flips, so that sample is integrated twice — which makes the period

```
2 * (data_end - loop_start) + 2
```

and not `2 * (data_end - loop_start)`. At that period the traversal repeats to within 1e-6 on **all
612 of 612** waves, with no measurable DC drift from pass to pass.

So the bake is exact and cheap: generate one full round trip through the engine's own ping-pong
path, emit it as an ordinary forward loop of that period, and set `sampleModes` to loop. It costs
`data_end - loop_start + 2` extra samples per affected wave. Getting the period wrong by those two
samples is not a subtle error — it moves the pass-to-pass difference from zero to 0.09–0.41.

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
5. **Sixteen velocity response curves become one of four modulator shapes.** Being per-partial they
   cannot be centralised in `DMOD`; §6 emits them per instrument instead, but the curve is still one
   of four and its range is 0–127 rather than the partial's own velocity window.
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
    and `--codec pcm` is portable and exact at 64.8 MB; §3 has the trade.

### Deliberate approximations

19. Reverse waves are baked reversed. (Ping-pong is *not* in this list: §7 measures it as exactly
    periodic, so its bake is lossless — it only costs sample data.)
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
