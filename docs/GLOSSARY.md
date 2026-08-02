# Glossary

Domain vocabulary for this project — reverse-engineering Roland **Sound Canvas VA**
(`SCCore.dll`) into a static, embeddable reimplementation of its synth engine.

Written for a technically literate reader who does not necessarily know sampler internals or
Roland's house terminology. Every SC-VA-specific number here is quoted from `FINDINGS.md` or read
off the Python resolvers (`scvx_directory.py`, `scvx_partials.py`, `scvx_engine.py`). Where
`FINDINGS.md` tags something `[likely]` or `[guess]`, that uncertainty is carried through.
Addresses are absolute with image base `0x180000000` unless stated as file offsets.

---

## The spine: how one MIDI note becomes audio

This chain is the whole project. Read it once and most of the rest of the glossary is just detail.

1. A **program change** (plus **bank select**) on a MIDI **part**, interpreted under a chosen
   **tone map** (SC-55 / SC-88 / SC-88Pro / SC-8820), is fed through a 3-level lookup (**LUT**) to
   get a **tone number**.
2. The **tone** is a 0x100-byte record: a 0x24 header (which contains the ASCII tone name) plus
   two **partial** parameter blocks of 0x6e bytes each.
3. Each **partial** names a **multisample** index and a **key center**, and carries a velocity
   range plus the entire synthesis back half (**TVA**, **TVF**, pitch envelope, LFO settings).
4. The **multisample** is a keyboard map: walk its key-split bounds with the transposed key to land
   in a **key zone**, which yields a **wave number** (with **velocity layer** alternates).
5. The **wave descriptor** for that wave number gives the **ROM** coordinates: region (1 MB bank
   slice), loop/end/start positions, **root key**, and **fine tune**.
6. Those coordinates address a compressed stream in the wave ROM embedded in `.rdata`, which the
   **block-floating-point DPCM** codec decodes to PCM.
7. The PCM is pitch-shifted to the played note, looped at its **sustain loop**, then shaped by
   **TVF** (filter) and **TVA** (amplitude envelope) and mixed.

In `FINDINGS.md`'s own words:
`(map,bank,prog,note,vel) → LUT → tone# → tone table → partials → multisample → key/vel zone →
wave# → wave descriptor → ROM coords → block-FP DPCM codec → PCM`.

`scvx_directory.resolve_midi()` implements steps 1–5; `scvx_engine.decode_wave()` step 6;
`scvx_engine.render_note()` step 7.

---

## Patch hierarchy (Roland / Sound Canvas vocabulary)

**Part** — Generally: one MIDI channel's worth of state in a multitimbral sound module (its current
program, volume, pan, effect sends, vibrato settings). In SC-VA: `part_start_voices`
@`0x180061a40` walks a part's linked list of active partials/tones (list head at part-struct
`+0x270`, links at `+0x108`) and starts one voice per partial `[likely]`. Part-level vibrato
parameters live around `part+0x3a8..0x3ae`.

**Program change** — The MIDI message that selects which sound a part plays (0–127). On its own it
is not enough to identify a Sound Canvas sound; it must be combined with bank select and the active
tone map.

**Bank select** — MIDI controllers CC0 (MSB) and CC32 (LSB) that extend program change beyond 128
sounds. On Sound Canvas hardware, CC32 selects the map family and CC0 selects the variation bank
within it. In this project's static LUT, `bank` is the MSB index and `tone_map` is the separate map
selector; see `program_to_tone(program, tone_map, bank)` in `scvx_directory.py`.

**Tone map** — A whole alternate program→sound table emulating a specific generation of Sound
Canvas hardware, so old MIDI files play with era-correct instruments. SC-VA ships five, named in
the companion `SCVSC.tnf` file: `0 Default`, `1 55Map` (SC-55), `2 88Map` (SC-88), `3 88ProMap`
(SC-88Pro), `4 8820Map` (SC-8820). The project's LUT uses `map`: 1=SC55, 2=SC88, 3=SC88Pro,
4=SC8820. Vintage accuracy is demonstrable: Piano 1's underlying sample moves region `r4` (SC55) →
`r5` (SC88) → `r8` (SC88Pro/8820) `[confirmed]`.

**LUT (the 3-level program→tone lookup)** — The mapping from `(map, bank, program)` to a tone
number, reversed from `FUN_180069200`:

```
tone# = s16( LUT3[ LUT2[ LUT1[map]*0x80 + bank ]*0x80 + program ] )
```

with `LUT1` @`0x1819f2e30`, `LUT2` @`0x1819f28b0`, `LUT3` @`0x1819f32b0` (exported as
`tables/lut1_2e30.bin`, `lut2_28b0.bin`, `lut3_32b0.bin`). `0xff` at a level means unassigned.
Validated 25/25 against the live engine `[confirmed]`.

**Tone** — Generally in Roland terminology: one complete playable sound, built from partials. In
SC-VA: a record in the melodic tone table at `.rdata 0x1818f2810`, **stride 0x100**, read by
`tone_lookup` @`0x1800026d0`. Layout = 0x24 header + **2 partial blocks × 0x6e**. The tone **name
is plain ASCII in header bytes [0..11]** and matches `SCVSC.tnf`'s 8820Map exactly (tone#0
"Piano 1", #39 "Harpsichord", #71 "Marimba") `[confirmed]`. Decoded by `scvx_directory.tone()`.

**Partial** — Generally: one sound-generating layer inside a tone (Roland tones classically stack
two or four partials, each with its own wave, filter and amplifier). In SC-VA: a 0x6e-byte
parameter block at `tone_base + 0x24 + partial_index*0x6e`. It holds the multisample index at `+2`
(`0xffff` = no partial), key center at `+4`, velocity range at `+0x4f`/`+0x51`, partial level at
`+0x50`, and every TVA/TVF/pitch/LFO field. Melodic tones have 2 partials; **drum tones use a
different table with stride 0x1e8 and 4 partials** — noted as an open loose end, not reversed.
Decoded by `scvx_partials.partial_params()`.

**Multisample** — Generally: a set of recorded samples spread across the keyboard (and often across
velocity) so an instrument does not have to be pitch-shifted unnaturally far from its recording
pitch. In SC-VA: a record in the table at `.rdata 0x1818ca570`, **stride 0x8c**, selected by
`multisample_select_wave` @`0x180003420`. Key-split upper bounds live at `+0x0c` (walk while
`key > bound`), the primary wave number is an `s16` at `+0x2c[zone]`, velocity-layer alternates at
`+0x2a`/`+0x2e`, fallback at `+0x6a`. Validated: the flute multisample #111 is ascending waves
797→809 `[confirmed]`. Implemented in `scvx_directory.msamp_wave()` / `multisample_zones()`.

**Wave** — In this project, one addressable compressed sample stream in the ROM, identified by a
wave number. 1868 unique waves across 24 regions were enumerated at fixed velocity; sweeping
velocity found **2089** (the extra 221 are velocity-layer-only waves). `rip_waves.py` ripped
2072/2089 straight from the DLL file.

**Wave descriptor** — The static record that turns a wave number into ROM coordinates. Table at
`.rdata 0x181897b40`, **stride 0x16**, decoded by `wavedesc_decode` @`0x18005ec90`. Fields:
`region = byte[0] & 0x7f`; `loop = (b[1]&0xf)<<16 | b[2]<<8 | b[3]`; `end` from bytes 7–9; `start`
from bytes 0xb–0xd (all 20-bit, region-relative, stored directly rather than accumulated);
**root key** = `byte[6]`; **fine** = `u16` at `[4]`; `flags` at `[0xa]` with bit1 = loop,
bit2 = reverse; ROM bank = `(region >> 4) & 1`. Validated against the flute: wave #806 → region 6,
loop 800928, end 803508, start 807803, root 75 `[confirmed]`. Implemented in
`scvx_directory.wave()`.

**Drum kit** — Generally: a "program" whose notes are unrelated percussion sounds rather than one
instrument transposed across the keyboard; each MIDI key is its own sample. In SC-VA the drum tone
table is separate (stride 0x1e8, 4 partials) and is explicitly **not yet reversed** — an open item.
Drum-key names (e.g. `[88] Standard 1 Kick 1`) live in the companion `.drk`/`.drf` files, not in
the DLL.

**GM (General MIDI)** — The cross-manufacturer standard that fixes 128 program numbers to named
instruments (program 0 = Acoustic Grand Piano, 119 = Reverse Cymbal) so a MIDI file sounds roughly
right on any device.

**GS** — Roland's superset of GM, adding bank-select variation sounds, extra drum kits, and a large
SysEx parameter set. Sound Canvas modules are the reference GS implementation, which is why bank
select and tone maps matter here at all.

**SysEx (System Exclusive)** — Manufacturer-specific MIDI messages of arbitrary length, used for
everything GM/GS channel messages cannot express (mode resets, effect settings, map forcing). In
SC-VA it arrives via the exported `TG_LongMidiIn(const uchar* sysEx, uint deltaFrames)`
@`0x1800895c0`.

---

## Sampling

**Sample** — One recorded waveform, stored here as a compressed stream in the wave ROM rather than
as plain PCM.

**ROM / wave ROM** — Generally: the read-only sample memory of a hardware sound module. In SC-VA
the "ROM" is **inside `SCCore.dll` itself**, in the `.rdata` section
(`0x180092000`–`0x181a08bff`, 25.5 MB of a 27 MB DLL). Roughly 24 MB of that is sample data
(entropy ≈ 7.5–7.8 bits/byte), with ~1.5 MB of structured tables after ~`0x181892000`.

**ROM bank** — Two base pointers, `g_wave_rom_base_a` @`0x181a18ef0` and `g_wave_rom_base_b`
@`0x181a11a68`, selected by `g_voice_wave_ctrl & 0x10`. As **file offsets** (validated
byte-identical against the live engine): **bank A `0x92700`**, **bank B `0x1092730`**. See
`scvx_engine.BASE`.

**Region** — A 1 MB slice of a ROM bank. `voice_setup_sample_playback` forms an address as
`(key & 0x7f) * 0x100000 | offset + base`, i.e. the ROM is banked in 1 MB regions. The static
form used by the ripper:

```
eff_region = region - 16*bank
base       = bankbase + eff_region*0x100000
```

24 regions are in use. Waves are packed sequentially within a region
(`wave[i+1].start ≈ align32(wave[i].end)`), which is why absolute positions were originally
believed to be accumulated at init — the descriptor table later proved they are stored directly.

**Key zone / key split** — A contiguous range of MIDI notes mapped to one wave; the boundary
between zones is a split point. In SC-VA the multisample's `+0x0c` array holds the **upper bound**
of each zone in transposed-key space. Empirically GM Piano switches wave about 12 times across the
keyboard.

**Transposed key** — SC-VA does not index the multisample with the raw MIDI note but with
`key = note + (0x40 - partial.keyCenter)`, i.e. the key center is the transposition origin that
re-centers the note onto the multisample's own key axis. `scvx_directory.tone_zones()` inverts this
(`nlo = tk_lo + kc - 0x40`) to report zone bounds back in MIDI note numbers.

**Key center** — Partial field at block `+0x04`; the origin of the transposition above. Distinct
from root key (below), which belongs to the wave, not the partial.

**Velocity layer** — Generally: alternate samples for the same key chosen by how hard the note is
struck (soft/mid/hard). In SC-VA these are the multisample alternates at `+0x2a`/`+0x2e`, plus the
partial-level velocity gate at block `+0x4f`/`+0x51`. Note 68 of GM Piano has three velocity
layers. Velocity-layer **crossfade** is listed as a remaining engine gap.

**Root key (unity key)** — The MIDI note at which a sample plays back at its recorded pitch —
playing that note requires no resampling. In SC-VA it is wave-descriptor `byte[6]` (flute wave #806
has root 75).

**Fine tune** — A fractional correction to the root key, since a recording is rarely exactly in
tune. Wave-descriptor `u16` at `[4]`. `scvx_engine.play_wave()` reconstructs native pitch as
`native = root + (1024 - fine)/1000` (milli-semitone domain), then
`ratio = 2^((note - native)/12)`. Verified to ~0.5% `[confirmed]`.

**Loop points / sustain loop** — Generally: a region of the sample replayed indefinitely so a held
note can sustain longer than the recording. In SC-VA the descriptor's three positions are, in the
engine's own naming, `loop` (data start = delta index 0), `end` (the loop point), `start` (the
physical end). The sustain loop is therefore `[end, start]`. Because the codec's predictor is an
integrator, looping works by **rewinding the delta index to the loop point while keeping the
predictor value**, so the waveform stays continuous at the seam — you do not stitch absolute
chunks `[confirmed]`.

**One-shot** — A sample that plays through once and stops (percussion, SFX). In `scvx_engine` a
wave is treated as looping whenever a real sustain region exists (`n - loopS > 64`); trusting the
descriptor's `flags & 2` loop bit was a **real bug** — it reads 0 for piano, so held notes ran out
one-shot and vanished at note-off.

**Reverse playback** — A second codec/sampler variant (`dpcm_voice_init_rev` @`0x18003ff90`,
runflag `0x22`/`0x24`, wave_ctrl bit 11) that runs the same DPCM accumulation backwards. Used for
reverse SFX — found at bank MSB=1, programs 119–121 (Reverse Cymbal = GM 119). Confirmed by ear.

**Interpolation** — Generally: computing sample values between stored samples when playing back at
a pitch other than the recorded one; the interpolator's quality is audible as the resampler's
timbre. In SC-VA every sampler uses a **4-tap FIR resampler** against `g_interp_coef_table`
@`0x181a0f210` (128 phases × 4 float taps), indexed by `(phase_frac >> 9) * 0x10`; raw wavetable
samples are normalized by `2^-27` (`7.450581e-09`). `FINDINGS.md` calls this "the actual sauce" —
`[confirmed]` structurally, `[likely]` that it is the timbre-defining element.
**`scvx_engine.py` currently uses plain linear interpolation instead**, an acknowledged gap.

---

## Synthesis (the "back half")

All of the following live in the same 0x6e partial block already exported to `tables/tone_a.bin` —
no separate ROM structure. Field offsets below are block-relative and come from `FINDINGS.md`'s
field map; `scvx_partials.partial_params()` names them, tagging `[c]` confirmed / `[l]` likely /
`[g]` guess.

**TVA (Time Variant Amplifier)** — Roland's name for the VCA: the stage that shapes loudness over
time. Fields: base level `+0x53`, level key-follow `+0x54`/`+0x55`, four envelope stage levels
`+0x5a..+0x5d`, rate key-follow rows `+0x65`/`+0x66`, rates `+0x67`/`+0x68`, rate velocity
sensitivity `+0x69`/`+0x6a`, second level key-follow `+0x6b`. Computed in the DLL by
`tva_compute_base_level` @`0x180060960` and `tva_compute_env_levels` @`0x180060b00`; reimplemented
in `scvx_engine.compute_tva_env()`.

**TVF (Time Variant Filter)** — Roland's name for the VCF: the filter whose cutoff moves over time,
controlling brightness. Fields: cutoff base `+0x2f`, cutoff key bias `+0x30`, **filter type**
`+0x31` (values 0/1/2/4/5/6 index `g_filter_type_coef` @`0x181987b00`, 7×4 B, type 0 =
passthrough; anything else bypasses), env-depth key-follow `+0x32`, env depth `+0x33`
(0x40-centered), five env levels `+0x3a..+0x3e`, env-rate key-follow `+0x46`, resonance `+0x4a`
(0x40-centered). Computed by `partial_compute_filter` @`0x180061210`.

**Cutoff** — The corner frequency above which a lowpass filter attenuates. Measured empirically to
be a **2-pole (−12 dB/oct) lowpass** (a 2-pole fit beat 1-pole with ~2× lower residual). Two laws
appear in the notes and they disagree — read carefully:
- Earlier, from a saw-only CC74 sweep: runtime cutoff is the u32 at `voice+0xc8` (max
  `245760 = 0x3C000` = fully open), `Fc ≈ 17640 × 2^((C − 245760)/14273)`, ±8%.
- **Superseding**, after A/B'ing piano as well: the runtime field is **`voice+0xcc`**, with
  `+0xcc = block[0x2f]*633.5 + 176882` and `Fc = 10591 × 2^((C − 245760)/14175)`, ~5% across piano
  *and* saw. `+0xc8` is described as fitting the saw "only by coincidence."
Either way the cutoff domain is **logarithmic in frequency**, roughly 14 300 units/octave
(~1190/semitone), full-open ≈ 18 kHz. Cutoff was found to be **note-independent** (no key-follow;
brightness stays flat via the multisample). See `scvx_engine.tvf_cutoff_hz()`.

**Resonance** — Emphasis at the filter's corner frequency. Partial field `+0x4a`, 0x40-centered,
mapped through `g_reso_curve` @`0x1819a2b88` (`tables/curve_reso_2b88.bin`; index 8 flagged for
recheck). `scvx_engine.apply_tvf()` approximates it as `Q = 0.707 * 2^((reso-64)/18)` — an
engineering approximation, not a reversed law.

**Pitch envelope** — A time-varying pitch offset (used for attack "blips", swells, drum pitch
drops). Computed by `partial_compute_pitch_env` @`0x18005fde0`: depth `+0x18`, five stage biases
`+0x1b..+0x1f` (each 0x40-centered), rate key-follow `+0x27`/`+0x29`/`+0x2a`, velocity `+0x2c`.
Coarse pitch itself is `+0x11` as `(v - 0x40) * 10` (milli-semitone × 10), with `+0x12` the
bend/LFO pitch depth (`partial_compute_pitch` @`0x18005fc20`). Not implemented in
`scvx_engine.py` yet.

**Envelope / envelope segment** — An envelope is a multi-stage contour applied to a parameter; each
stage is a **segment** defined by a target level and a rate. SC-VA's TVA envelope has **4 segments**
(levels `block[0x5a..0x5d]`, rates `block[0x5e..0x61]`) plus a **release** rate at `block[0x62]`.
Note the classic ADSR names map loosely: segment 0 behaves as attack, later segments as hold/decay
toward a sustain plateau, and the release is spliced in at note-off.

**Attack / decay / sustain / release** — The four classic envelope phases: rise to peak after
key-down, fall to a steady level, hold while the key is held, fade after key-up. Validated example
from the reimplementation: attack 0 ms, hold 626 ms, decay 16.8 s — matching the engine's live
envelope output to ~2%.

**Envelope generator (the timing model)** — `env_ramp_segment` @`0x180083a70` is a **16-bit
phase accumulator** on the env-state block at `voice+0xc`: `+0x06` rate, `+0x08`/`+0x0a` segment
start/target level, `+0x0c` current output, `+0x0e` phase. Per tick
`phase += rate × (g_env_block_speed + carry)`; when phase wraps past `0xffff` the segment completes
and the next loads, otherwise the output interpolates start→target by phase. Hence
**`t_segment = (0x10000 / (rate × speed)) × 10 ms`** (speed normally 1) `[confirmed]`, calibrated
empirically. Interpolation within a segment uses `g_env_shape` (a fast-approach curve,
`tables/env_shape_7a90.bin`), applied in the gain domain.

**Control rate / control tick** — The (much lower) rate at which envelopes, LFOs and other
modulators are recomputed, as opposed to the per-sample audio rate. In SC-VA the internal render
rate is a fixed **32 000 Hz**, `render_block` processes **32 samples**, and the control update runs
every 10 blocks → **`control_block_samples = 320`, control rate = 100 Hz, Δt_tick = 10 ms**
`[confirmed]`, measured by watching the live phase accumulator step by exactly `rate` every 320
samples. Two independent confirmations: the envelope segment maths and the LFO's round 0.1 Hz
per-unit rate steps. Chain: `control_tick_dispatch` @`0x18008f0d0` → `voices_control_update`
@`0x1800849a0` (64 voices, stride 0x220) → `voice_block_process` @`0x180080e40` →
`env_ramp_segment`.

**LFO (low-frequency oscillator)** — A sub-audio oscillator used to modulate pitch (vibrato),
filter, or amplitude (tremolo). SC-VA's LFO reuses the same 100 Hz phase accumulator
(`lfo_advance_waveform` @`0x180082a30`; `lfo_update` @`0x180081b90` writes per-voice mod triples
`{pitch, TVF, TVA}` read at `voice+0x170/0x180/0x198`). Rate: `f = rate × 100 / 65536` with
`rate = g_lfo_rate_tbl[param]` @`0x1819a2790` — 0–20 Hz, a clean 0.1 Hz per unit up to ~8 Hz then
accelerating. Waveforms (`g_lfo_waveform_sel` @`0x181a227d0`): 0 sine, 1 random sample-and-hold,
2/3 slewed random, 4 square, 5 sawtooth, 6 triangle. Pitch depth via `g_lfo_cents_tbl`
@`0x1819a2690` = **10 cents/unit**, up to ±6000 cents. Mod wheel (CC1) adds on top of the part's
vibrato parameters. Caution: the function once labelled `lfo_value` @`0x18008fbb0` is actually a
**Galois LFSR PRNG** (renamed `prng_lfsr`) feeding the random shapes — not the oscillator. Not yet
implemented in `scvx_engine.py`.

**Key follow (key scaling)** — Making a parameter depend on which note is played (e.g. shorter
decay high up the keyboard, as on a real piano). In SC-VA these are 2D tables indexed by
`row * 0x80 + key`: `g_kf_pitch`, `g_kf_tvfenv`, `g_kf_tvalevel`, `g_kf_tvarate0/1`, `g_kf_tvfrate`,
`g_kf_pitchrate0/1`, exported as `tables/kf_*.bin`. The partial block stores which row to use
(e.g. TVA rate rows at `+0x65`/`+0x66`). Note the empirical finding that **TVF cutoff has no key
follow** in practice.

**Velocity sensitivity** — How much note-on velocity modulates a parameter. TVA rate velocity
sensitivity sits at `+0x69`/`+0x6a` and is applied through `env_level_scale`. Distinct from the
velocity *gate* (`+0x4f`/`+0x51`), which decides whether the partial sounds at all.

**Shared converters** — Two 0x40-centered scaling helpers reused across the back half, both
returning 8.8 fixed point where `0x100` = 1.0 (neutral):
`env_rate_scale` @`0x1800607e0` (base rate + modifier → rate multiplier, via `g_env_scale_curve`
and `g_env_rate_out`) and `env_level_scale` @`0x180060880` (same family for depths). Both are
reimplemented literally in `scvx_engine.py`.

**Curve tables** — Static lookup tables that turn parameter bytes into physical scalings, exported
under `tables/`:
- `g_env_rate_out` @`0x1819a3060` — an exact `2^((i − 0x80)/32)` exponential rate table
  (`[0x40]=2⁻²`, `[0x80]=2⁰=0x100`, `[0xa0]=2¹`, `[0xc0]=2²`).
- `g_level_curve` @`0x1819a2a00` — monotonic dB-style level → 16-bit log attenuation
  (index 0 = silence, 127 = full).
- `g_amp_curve_hi`/`_lo` — 16-bit log level → linear gain (`0xffff` → 0 dB, `0x8000` → −42 dB);
  `scvx_engine.amp_of()` computes `hi[l>>8] * lo[l&0xff] >> 16`.
- `g_filter_type_coef` @`0x181987b00`, `g_reso_curve` @`0x1819a2b88`, `g_env_shape`,
  `g_rate_curve`, `g_env_scale_curve`, the `kf_*` key-follow tables, and the LFO wave/rate/cents
  tables.

**Voice** — One simultaneously sounding note-layer. SC-VA has **64 voices** (bound checked
`< 0x40` in `voice_setup_sample_playback`). One MIDI note can consume several voices — one per
sounding partial. Voice state exists in two parallel forms: `g_voice_run_flags` @`0x181a1b5b8`
(stride 0x50, bit 0 = running, counted by the export `TG_XPgetCurTotalRunningVoices`) and a
structure-of-arrays hot state around `0x181a6f60…0x181a723xx`, which is why `render_block`
processes voices in groups of 4.

---

## Codec

**Block floating point** — A compression scheme where a block of samples shares one exponent
(scale) and each sample keeps only a small mantissa, giving wide dynamic range at low bitrate. In
SC-VA the exponent is a **4-bit nibble per 16-sample block**.

**DPCM (differential PCM)** — Storing the *difference* between consecutive samples instead of their
absolute values, since audio is locally smooth and deltas are small.

**The SC-VA codec** — Block-floating-point DPCM with two parallel streams, reversed from
`FUN_18003f4e0` (sampler init) and `sample_fetch_loop_wrap` @`0x18003f870`. Note this **corrected
an earlier guess of "ADPCM with a step table."** `[confirmed]`:

```
predictor = 0  (int32)
for each output sample i:
    scale_byte = scaleStream[i >> 5]                    # one byte per 32 samples
    scale      = ((i >> 4) & 1) ? (scale_byte >> 4) : (scale_byte & 0xF)
    delta      = (int8) deltaStream[i]                  # one signed byte per sample
    predictor += delta << (scale + 10)
    output     = predictor * 2^-27                      # 7.450581e-09
```

Roughly 8.1 bits/sample, matching the ~7.6-bit `.rdata` entropy.
Vectorized in `scvx_engine.decode_wave()` as a `numpy.cumsum`.

**Delta stream** — The per-sample signed bytes; per-voice pointer at sampler-state `+0x20`. Static
address: `base + (loop_start & ~0x1f)`.

**Scale stream** — The exponent nibbles, two blocks per byte; per-voice pointer at sampler-state
`+0x38`. Static address: `base + ((loop_start & ~0x1f) >> 5)`.

**Predictor** — The running int32 accumulator that integrates the deltas. Because it is a *pure*
integrator it accumulates slow DC drift, which the engine removes with a downstream DC-blocking
high-pass. Keeping the predictor value across a loop rewind is what makes the loop seam continuous.

**Scale nibble** — The 4-bit shift exponent for one 16-sample block, selected from the scale byte
by `(i >> 4) & 1`.

---

## Reverse-engineering

**Ghidra** — The NSA's open-source software reverse-engineering suite. This project drove Ghidra
12.1.2 in **headless** mode against the stripped 64-bit `SCCore.dll` (1045 functions, after
`DefineTableFunctions.java` recovers the table-dispatched ones auto-analysis cannot reach), applying
function/label renames via scripts (`RenameSynth`, `RenameEnvEngine`, `RenameLfo`) in `tools/ghidra_scripts`.

**Decompilation** — Ghidra's reconstruction of C-like source from machine code, dumped here to
`SCCore.decompiled.c` (~2.7 MB). Important caveat from `FINDINGS.md`'s honesty preamble: variable
names, types and control flow are *approximations*; the original source, symbols and comments are
gone. All line references are to that regenerated file. Names beginning `FUN_` / `DAT_` are
Ghidra's auto-generated placeholders; readable names like `sampler_pcm` or `render_block` are
**this project's labels, hypotheses fitted to observed behavior — not Roland's**. The `TG_*` names
are the exception: they are genuine PE export-table symbols.

**Confidence tags** — `FINDINGS.md` marks each claim `[confirmed]` (the code plainly does this),
`[likely]` (strong inference), or `[guess]` (plausible, thin evidence). Several `[guess]`-level
claims were later overturned in-document — e.g. `0x180056560` was mislabeled `fx_process` when it
is actually configuration (`fx_program_load`); the real per-sample DSP is `0x18008c2c0`.

**Static reconstruction** — Deriving behavior purely from the binary's contents: parse the ROM
tables, decode the samples, compute the parameters, with `SCCore.dll` never loaded. This is the
project's goal, since the product is discontinued. `scvx_directory.py`, `scvx_partials.py` and
`scvx_engine.py` are all static in this sense (`scvx_engine.py` reads the DLL only as a *data
file* for the wave ROM).

**Engine-query reconstruction** — The contrasting method: run the real DLL, play notes, and read
the resulting voice state out of process memory to learn what the engine chose. It gives ground
truth about *coordinates* but is contamination-prone — stale/phantom voices mislabeled zones during
the multisample mapping (the samples always decoded correctly; only the *labeling* was wrong). The
static tables superseded it, and every static layer was then validated byte-exact against it.

**A/B validation** — Rendering the same note sequence through both the real DLL (via
`tools/decoder`'s `scdec song`, and the sibling `SauceForYourEars` .NET host) and the Python
reimplementation, then comparing spectra and contours. Artifacts in the repo:
`real_engine_piano.wav` vs `our_engine_piano.wav`, and the SC-55-map pair. This is what surfaced
the two shipped-silently bugs (the loop flag and the TVA base level).

**Calibration** — Turning reversed raw byte domains into physical units (rate → ms, cutoff → Hz) by
measuring the running engine. Done for the envelope tick (10 ms), the TVF cutoff law, and the LFO
rate/depth. The `scvx_partials.py` field map is described as "confirmed map, uncalibrated units" —
the offsets are known; not every byte→unit law is.

**Honest boundary** — A phrase used throughout `FINDINGS.md` to mark exactly where proof stops and
inference begins (e.g. "the codec is 100% ours and proven" but the sample *locations* were
initially obtained from the running engine). Worth preserving when extending the notes.

---

## Known-uncertain and open items

Carried forward from `FINDINGS.md` so the glossary does not overstate:

- **`voice+0xc8` vs `voice+0xcc`** as the runtime TVF cutoff field — both appear, with the later
  `+0xcc` universal law explicitly superseding the earlier saw-fitted `+0xc8` one.
- **Drum tones** (4 partials, stride 0x1e8) — not reversed.
- **Pitch envelope and LFO** — SOLVED and implemented in `scvx_engine.py` (validated bit-exact /
  to ~1 cent against live voice state). LFO routed to all three destinations; pitch env absolute.
- **4-tap FIR interpolation** — IMPLEMENTED (`_interp4`, 7-bit phase index, no inter-row interp,
  matches the engine). No longer linear.
- **TVF cutoff envelope**, **velocity-layer crossfade** — SOLVED (`compute_tvf_env`,
  `partial_level`). The loop has no crossfade; the engine loops in the delta domain.
- **`g_reso_curve[8]`** — flagged for recheck.
- **Which `voice_ctrl_ramp_*` drives pitch vs amp vs filter** — still unconfirmed; those functions
  are named by pipeline position, not by confirmed target.
- **Effect algorithms** — 67 `fx_algo_*` located and named via `g_fx_type_to_algo_map`
  @`0x18189566c`, but none dissected. Dispatch slot **66** (`fx_algo_orphan66_moddelay`
  @`0x180029c90`) is a complete, unreachable, modulated multi-tap delay; why it exists is `[guess]`.
- **Nothing has been validated with a live debugger** — everything is static analysis plus
  spot-checks against the engine's own output.

---

## Sources for the general (non-SC-VA) definitions

- [Roland D-110 tone editing — partial / TVF / TVA structure](https://untidymusic.com/roland-d110/roland-d-110-tone-editing)
- [Roland D-50 (Wikipedia) — TVF/TVA as Roland's VCF/VCA naming](https://en.wikipedia.org/wiki/Roland_D-50)
- [Roland GS (Wikipedia) — GS as Roland's GM superset](https://en.wikipedia.org/wiki/Roland_GS)
- [Roland SC-55 (Wikipedia)](https://en.wikipedia.org/wiki/Roland_SC-55)
- [VOGONS — SC-88 in SC-55 map mode, bank select MSB/LSB and map switching](https://www.vogons.org/viewtopic.php?t=59395)
- [DTM Wiki — Roland Sound Canvas VA (SC-8820 recreation, four sound maps, discontinued Sept 2024)](https://dtm.noyu.me/wiki/Roland_Sound_Canvas_VA)

All SC-VA-specific addresses, strides, offsets and laws come from this repository's own
`FINDINGS.md` and Python resolvers.
