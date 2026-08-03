# Deconstructing the Sauce — SCCore.dll reverse-engineering notes

## Honesty preamble (read this first)

These notes were written by **Claude, an AI model, driving Ghidra 12.1.2 in headless mode** —
begun by Anthropic's Opus 4.8 (including its 1M-context variant) and continued by Claude Fable 5,
which recovered the TVP runtime machine, the hold clock, the damper and sostenuto pedals, the
table-dispatched functions Ghidra could not reach, drum NRPN key-follow, portamento, random pan,
and the 32-part finding. The git history records which model wrote which finding. Treat them
accordingly:

- **This is decompiler output plus inference, not ground truth.** Ghidra reconstructs C from
  machine code; variable names, types, and control flow are *approximations*. The original
  source, symbols, and comments are gone (the DLL is stripped).
- **The function names in the `scvx` project are my labels, not Roland's.** I named things like
  `render_block` / `sampler_pcm` / `g_interp_coef_table` from behavioral evidence. They are
  hypotheses that fit the code I read — they are not authoritative and some may be wrong.
- **I can be confidently wrong.** Where I say "this is X," it means the evidence strongly
  suggested X to me — which is not the same as having verified it.
- **Not everything here rests on the same kind of evidence.** The earliest findings are pure
  static reading: no binary run, no hypothesis A/B-tested against real audio. Later ones are
  measured against the *running* engine through the `scdec` harness in `tools/decoder` — envelope
  and filter traces, live LFO dumps per control tick, effect coefficient harvests, and whole-song
  A/B renders, executed under Wine where no Windows host was to hand. Where a finding says it was
  measured, it was; where it does not, it was reasoned.
- **Confidence is tagged** per finding: `[confirmed]` = the code plainly does this;
  `[likely]` = strong inference; `[guess]` = plausible but thin evidence.
- **Provenance:** all line numbers refer to `SCCore.decompiled.c` (regenerated whenever names
  change). Addresses are absolute (image base `0x180000000`). Verify anything important yourself.

Context: `SCCore.dll` is the synth core of Roland **Sound Canvas VA** (discontinued, no longer
sold). 64-bit x86, stripped, 1045 functions (830 from auto-analysis alone; the rest are reached
only through data pointer tables). `SOUND Canvas VA.bin` is the VST shell (ignored).

---

## Render architecture (overview)

`render_block` @ `0x18008b1d0` processes active voices in **groups of 4**, gated by a
voice-active bitmask. Per group it runs four stages: `[confirmed]`

1. build active-voice mask
2. `voice_ctrl_ramp_a/b` — per-sample control-value ramps (pitch/amp) `[likely]`
3. `voice_render_dispatch` — the oscillator `[confirmed]`
4. `voice_ctrl_ramp_c/d` — more control ramps (filter?) `[likely]`

### Oscillator — `voice_render_dispatch` @ `0x18003f720` `[confirmed]`
Dispatches on voice format flags to one of six samplers:
- bits `&6` select sample format: `0` PCM, `2` 4-bit ADPCM-style nibble, `4` fmt4
- bit `0x20` selects mode A vs mode B (`_alt` variants)
- inactive voices get an **anti-denormal fill** of `0x3727c5ac` (≈ 1e-5) — a tell that a
  float mix/reverb bus runs downstream. `[likely]`

Samplers: `sampler_pcm`@18003f9d0, `sampler_adpcm4`@18003fb80, `sampler_fmt4`@18003fdd0,
and `_alt` variants @180040210 / 1800403c0 / 180040610.

### Interpolation core — the actual "sauce" `[confirmed]`
Inside every sampler:
```c
lVar = (phase_frac >> 9) * 0x10;   // 7-bit phase index -> 128 entries, 16 bytes each
out = ( coef[0]*s0 + coef[1]*s1 + coef[2]*s2 + coef[3]*s3 ) * gain;
```
- **4-tap FIR resampler** against `g_interp_coef_table` @ `0x181a0f210` (128 phases × 4 float
  taps). This is what gives SC-VA its characteristic resampling timbre. `[confirmed]` structure,
  `[likely]` that it's the timbre-defining element.
- Raw wavetable samples normalized by `2^-27` (`7.450581e-09`). `[confirmed]`
- `sampler_adpcm4` unpacks 4-bit nibbles (`& 0xf` / `>> 4`) → compressed waveROM. `[confirmed]`
- The 4-sample history window is `param_1` (a `[16]` = 4 floats), shifted one tap per whole
  sample advanced. `[confirmed]`

`sample_fetch_loop_wrap` @ `0x18003f870` handles sample fetch + loop-point wrap. `[likely]`

### Control ramps `[likely]`
`voice_ctrl_ramp_a/b` @ 18005e040/18005e990 and `_c/_d` @ 18005d8d0/18005dbf0 are
per-sample segment interpolators: hold a value when a voice's active bit is clear, else ramp
toward a target. They smooth control parameters. Which ramp drives pitch vs amp vs filter is
**not yet pinned down** — named by pipeline position, not by confirmed target.

---

## Master audio loop — `TG_Process` @ `0x180088ca0` `[confirmed]`

**Correction to an earlier guess:** I first wrote that `TG_Process` "was already named by a prior
analysis pass." That was wrong. It — and 10 sibling `TG_*` functions — are **exported symbols in
the DLL's PE export table, auto-named by Ghidra on import**. No mystery prior analyst. I found
this out when the user pointed me at `../SauceForYourEars/native/SCCore.{h,cpp}`, a separate
project that loads `SCCore.dll` via `dlsym` and gives the **ground-truth C signatures** for the
exports. Those signatures *confirm* (not just suggest) the architecture below.

**Ground-truth exports** (from `SCCore.h`), all present in the decompilation:
| Export | Addr | Signature |
|---|---|---|
| `TG_initialize` | 1800888a0 | `int(int)` — 0 arg, negative on failure |
| `TG_activate` | 180088b40 | `void(float sampleRate, int blockSize)` |
| `TG_deactivate` | 180088b90 | `void()` |
| `TG_setSampleRate` | 180088bb0 | `void(float)` |
| `TG_setMaxBlockSize` | 180088bf0 | `void(uint)` |
| `TG_flushMidi` | 1800891e0 | `void()` |
| `TG_ShortMidiIn` | 180089370 | `void(uint eventCode, uint deltaFrames)` |
| `TG_LongMidiIn` | 1800895c0 | `void(const uchar* sysEx, uint deltaFrames)` |
| `TG_XPgetCurTotalRunningVoices` | 18008ab80 | `uint()` (thunk) |
| `TG_Process` | 180088ca0 | `void(float* left, float* right, uint count)` |

(`TG_setInterruptThreadIdAtThisTime` is exported too but not surfaced by that name in the dump.)

`TG_Process` is the master per-block loop and calls, in order:

```c
render_block();        // synthesize all voices  -> dry/wet buses
fx_process_block();    // apply effects DSP
FUN_18008aca0();       // per-block housekeeping
```

Followed by a deferred-event dispatch (bitmask `g_fx?`/`DAT_181a1de7c` selecting callbacks from
a function-pointer table) — this is where the "2 flag" (10000-sample tick) etc. get serviced.

---

## Effects engine

### CORRECTION — I initially mislabeled this. `[confirmed correction]`
In my previous pass I named `0x180056560` `fx_process` and called it "the vectorized effects
DSP." **That was wrong.** `0x180056560` is *configuration* — it programs coefficient registers
from macro parameters and loads a preset table. The actual per-sample DSP engine is a different
function, `0x18008c2c0`. I've renamed both. This is exactly the kind of confident-but-wrong
inference the preamble warns about; I caught it by tracing what `TG_Process` actually calls
rather than trusting the name I'd already applied. Corrected names below.

### The real engine — `fx_process_block` @ `0x18008c2c0` `[confirmed]`
Called by `TG_Process` every block. Structure:
- A **33-tap (`0x21`) accumulation** across a strided float buffer (early-reflection / FIR-style
  mix), 4 lanes at a time, `1e-05` anti-denormal seed. `[confirmed]` shape, `[likely]` purpose.
- Then a **32-sample sub-block loop** that dispatches to the selected algorithm:
  ```c
  pcVar = g_fx_algo_dispatch[g_fx_algo_index];      // per-algorithm processor
  (*pcVar)(inL*2, inR*2, state, out, delayBufA, delayBufB, &g_fx_coef_f32);
  *state *= 0.5;                                      // mix/feedback scale
  ```
- Two circular delay-line buffers (advanced/wrapped by `fx_delayline_wrap` @ `0x180089830`)
  plus the shared float coefficient array feed every algorithm. `[confirmed]`

### Effect algorithm model `[confirmed]`
- `g_fx_algo_dispatch` (`0x181895190`) is a **function-pointer table of 67 per-algorithm DSP
  processors**, indexed by `g_fx_algo_index` (`0x181a63460`). All 67 targets are distinct
  functions. `[confirmed]`

### Naming the 67 algorithms — done, and a trap I avoided `[confirmed]`
The dispatch index is **NOT** the EFX type number. I nearly assumed it was (Thru=0, Stereo-EQ=1,
…) — the size heuristic even looked vaguely supportive — but the correlation was weak (Pearson
r=0.35) so I did not commit to it. Instead I found the authoritative mapping in the binary:

- `fx_select_algo_from_type` @ `0x18003f140` scans `g_fx_type_to_algo_map` for the current
  `g_fx_current_type`, then calls `fx_set_algo_index` @ `0x180062410`.

**CORRECTION — the record starts 12 bytes before the symbol, and it carries the names.**
`[confirmed]` I first read this table from `0x18189566c`, where the `g_fx_type_to_algo_map` symbol
lands, and described the record as `[+0] type key (MSB<<8|LSB)`, `[+2] dispatch index`. That is the
middle of the record. The symbol points at the type key, not at the record start, so dumping from
it reads each effect's name against the **previous** effect's type key — which is exactly why this
looked like a table of bare numbers that needed an external name source. From the true start at
`0x181895660`, 66 records × 0x28 bytes:

| Offset | Field |
| --- | --- |
| `+0x00` | `char name[12]` — display name, space padded |
| `+0x0C` | `u16` type key (MSB<<8 \| LSB) — what `40 03 00` selects |
| `+0x0E` | `u16` dispatch index into `g_fx_algo_dispatch` |
| `+0x10` | `param_apply` — per-effect handler mapping the 20 GS parameters to registers |
| `+0x18` | `param_defaults` — returns a block whose `+0x0C` holds the `0x1C`-byte defaults |
| `+0x20` | `common` — one shared handler, identical in all 66 records |

So **no external name source is needed**: the engine names its own effects, and
`tools/dump_efx_table.py` recovers the whole directory from the DLL. The 66 records are the 65
types the SC-8820 manual lists plus a `0xffff` record with a blank name and a **null** apply
handler, which is the "no effect assigned" state; record 66 reads as noise, which is what pins the
count. The names agree with the manual's Insertion Effect List on all 65 types, with two cosmetic
differences — the DLL says `Equalizer` where the manual says `01: Stereo-EQ`, and `Lo-Fi` where the
manual says `33: Lo-Fi 1`.

The mapping is still a **scramble** — Stereo-EQ (`01 00`) → dispatch **2**, Spectrum (`01 01`) →
dispatch **6**, Humanizer (`01 03`) → dispatch **46**, 3D Manual (`01 71`) → dispatch **48**. Had I
named by EFX order, ~all 65 would have been wrong. Two lessons rather than one: *verify the mapping
in the binary, don't infer it from order or size* — and *verify where a symbol sits inside its
record before trusting the field at offset zero.*

All 67 dispatch functions are now named `fx_algo_*` in the project (`sampler`-style):
- dispatch **0** = `fx_algo_thru` (smallest fn, 529 B — routing/level only, confirms index 0 = Thru)
- dispatch **1** = `fx_algo_none_placeholder` (reached only via key `0xffff` = no effect assigned)
- dispatch **2–65** = the 64 real EFX effects, from the type-map join `[confirmed]`
- dispatch **66** = `fx_algo_orphan66_moddelay` — investigated in depth, see below.

Notable: `fx_algo_3d_manual` and `fx_algo_3d_auto` are the two ~9.4 KB giants despite few user
params — consistent with HRTF/binaural convolution being DSP-heavy. `[likely]`

Cross-source name discrepancies exist (manual vs `parameter2.dat`), e.g. EFX#53 "Bass Multi" chain
is `(EH-PH-CF-Dly)` in the manual but `(Comp-OD-CF-Dly)` in `parameter2.dat`. I used the manual's
names for the slugs. `[confirmed]` the discrepancy; the effect identity is the same.

I have **not** analyzed the internal DSP of any single algorithm (reverb topology, delay times,
etc.) — only identified and named them, with one exception: the orphan below.

### The orphaned algorithm — `fx_algo_orphan66_moddelay` @ `0x180029c90`

Dispatch slot **66** is a fully-formed effect that nothing can select. Findings:

- **Unreachable — proven, not assumed.** `[confirmed]`
  - No direct caller anywhere (only the dispatch-table pointer references it).
  - `g_fx_algo_index` is written in exactly two places: `= 0`, and `= arg` inside
    `fx_set_algo_index`. The arg ultimately comes from `g_fx_type_to_algo_map`, whose values top
    out at **65**. A type that matches no map entry leaves the index at its initial **0** (Thru),
    never 66. So no MIDI/SysEx EFX selection can ever land on slot 66.
- **It's a complete, real effect, not a stub or padding.** `[confirmed]` Same 7-arg algo ABI
  (`inL, inR, state, out, delayBuf, coef`) as the other 66; reads ~0x3a0 bytes of coefficients.
- **DSP class: modulated multi-tap delay / chorus-ensemble.** `[likely]`
  - Writes delay taps at `param_6[0x4000]`, `[0x8000]`, `[0xc000]` and an unusual `[0x5555]`.
  - Runs 4 triangle-wave LFOs — each a phase accumulator wrapped into `[-1,1)` by the
    `do { … x += 2.0 } while` idiom, then `abs()`'d into a triangle, then waveshaped/saturated.
- **Not a duplicate of any exposed effect.** `[confirmed]` The `param_6[0x5555]` tap occurs in
  **no other algorithm**; its tap layout is unique. It is its own distinct implementation.
- **Best guess at why it exists:** a hidden/reserved effect carried in the shared Roland DSP
  codebase (SC-VA emulates the SC-8820/SC-88Pro engine) that was simply never wired into SC-VA's
  exposed EFX type list. `[guess]` — I have no documentary evidence, only the code shape. Could
  equally be a dev/test algorithm or a leftover from a sibling hardware model.

A plate comment recording all of the above is attached to the function in the `scvx` project.

### Coefficient / register interface `[confirmed]`
Macro parameters → registers → float coefficients, via:
- `fx_reg_write` @ `0x1800898d0` — writes a register; converts the byte to a float in
  `g_fx_coef_f32` (`0x181a1af70`) using scale factors (1/128, 1/32768, 1/32, 1/8192) chosen by
  a per-register type map that depends on `g_fx_algo_index`. Also stores raw bytes for shadowing.
- `fx_reg_write_slew` @ `0x1800621f0` — **slewed** write: steps the value one unit per call
  toward the target (parameter smoothing to avoid zipper noise). `[likely]` the smoothing intent.
- `fx_reg_write16` @ `0x180062050` — writes a 16-bit value across two consecutive registers.
- `g_fx_reg_shadow` (`0x181a73cc0`) — mirror of written register bytes (used for change detection).

### Config handlers `[likely]`
- `fx_program_load` @ `0x180056560` (was mislabeled `fx_process`) — on macro-param change
  (reverb type `[0x10]`, chorus type `[0x11]`, param `[0x13]`, etc.) programs the register set
  and SIMD-unpacks a packed int16 preset table into the coefficient area.
- `fx_param_apply` @ `0x180055e90` (was `fx_param_update`) — lighter previous-vs-current param
  delta handler. Both are invoked indirectly (dispatch table), so they show no direct callers.

### The chorus tap: base confirmed live, my modulation arithmetic corrected `[open, narrowed]`

The macro row's delay byte becomes a tap base through `base = tap_base((delay * 3) - 0x8000)`,
12.12 fixed point. **The live engine confirms it**: `scdec chodump` of the GM default reads back
`tap1 base=1966080` — 480 samples — exactly what the row computes, with `writeIn=1 tapOut=1
fbCoef=0.0625` also matching. The static coefficients are not where any wet discrepancy lives.

**Correction.** The previous revision of this section claimed the tap modulation was "±0.2 samples
at full swing" and therefore irrelevant. That was wrong by a factor of 500: the depth multiplies
the triangle *before* the 12.12 split — `((800 × |saw24|) >> 14)` reaches 409600, a **0–100 sample
swing with mean +50** — so the effective tap delay is 480–580 samples, mean 530, sweeping over a
2.73 s period (`inc=192` into 24-bit phase). I read the depth byte as if it were the final offset.

That correction also demotes the "chorus is 13 ms late" reading. Cross-correlating two chorus
returns whose LFOs free-run from different startup histories compares taps at *different points of
a ±50-sample sweep*; over a 0.8 s window — a third of the LFO period — the apparent lag is a
function of the phase difference, not of the base delay. The measured 424 samples is not a reliable
estimate of anything. What survives of that measurement: the wet is **1.17 dB low**, identically at
every pan and in both channels, and that is still unexplained.

**The R-companion stage measured: it contributes nothing.** `[confirmed]` The two stages have
disjoint signatures — the L tap's delay is 480–580 samples, the R stage's dump taps decode to
~8–11 — so the wet's onset betrays which taps exist. Isolating the wet by subtraction around a
note's attack transient (the one non-periodic feature; a sustained tone's pitch period defeats any
lag scan):

| Window after dry onset | DLL wet RMS | reimpl. wet RMS |
| --- | --- | --- |
| 0–440 samples | **0.0–0.1** (noise) | 0.0 |
| 440–600 | 237.2 | 179.5 |
| 600–900 | 348.6 | 251.4 |

The DLL's wet is silent until the L tap's arrival window, in both engines alike. A live R stage
would have placed energy within 60 samples of onset; there is none. The "gated off" claim and the
spec's ±2.5% L-only calibration both survive; the dump showing the stage armed (`writeIn=1,
tapOut=1`) must mean `toR=0` gates its input, and an armed-but-starved stage dumps plausible
coefficients while producing nothing.

**The phase-matched render settled the rest: there is no chorus level defect either.** `[confirmed]`
Both engines advance the LFO at 192/sample from engine start, so shifting our note later in our own
stream sweeps our LFO phase-at-onset across the DLL's. Sweeping that shift over the full
87,381-sample period and measuring the isolated wet each time:

| Shift | wet-vs-wet r | overall wet level |
| --- | --- | --- |
| 8000 (control) | 0.35 | −1.17 dB |
| 20800 | 0.73 | — |
| 39200 | 0.80 | −0.15 dB |
| **40000** | **0.82** | **−0.04 dB** |
| 41600 | 0.77 | +0.26 dB |

The level difference crosses zero exactly at the correlation peak — the signature of pure phase
mismatch, not of any gain error. **The "1.17 dB deficit" was the phase offset too.** With the static
coefficients matching the live dump, the tap structure matching at onset, and the level matching to
0.04 dB at matched phase, the chorus implementation has no measurable defect at all.

Two consequences worth more than the finding. First, the methodological one: a free-running LFO
makes the chorus wet's *level* a function of the phase offset between the engines — up to ±1.5 dB
of the wet in windowed measurements — so **every whole-file comparison of chorus-heavy material
carries a phase-dependent bias** that no amount of averaging removes, because the offset is
constant per file. Tier-2 digests over chorus material are impossible until the harness pins the
phase. Second, the open engineering item: the optimum shift was 40,000 samples where warm-up plus
queue latency predicts 3,225 — so the DLL's LFO does **not** start at phase zero at activate, and
the ~36,800-sample discrepancy is unaccounted init state. Pinning it (a phase dump at song start)
is what would let the harness phase-match deterministically instead of by sweep.

*Attempted:* the harness now reads `L lfoPhase` at song start (deterministic — identical across
runs, 2,992,128 after the standard warm-up) and an experimental `pin` argument pre-rolls the engine
until the register wraps to ~0. **Validation failed**: pinned-register-zero against an
accumulator-zero reimplementation still leaves −2.7 dB of wet at r 0.73 and lag 142 — so the
register's zero is not the accumulator's zero, and decent waveform alignment can coexist with a
3 dB wet difference, which also breaks the simple "level tracks phase" model above. The convention
is unresolved, and resolving it means modelling the engine's LFO state exactly.

The project's conclusion, rather than pushing further down that hole: phase differences in
free-running modulated effects are not worth modelling short of total state replication. Render
comparison should move to **phase-tolerant metrics** — per-band spectrum and envelope PSNR, with a
small alignment search absorbing fixed delays — and reserve exactness claims for the deterministic
layers. The sweep result stands as the proof that the chorus *implementation* is correct; the pin
mode stands as the deterministic phase *read*, useful to whoever eventually resolves the origin.

Incidentally measured: the DLL's dry onset lands 153 samples after the reimplementation's for the
same nominal event time — its input queue costs ~5 ms of fixed latency. Irrelevant once measured
onset-relative, but worth knowing before reading any absolute-time comparison.

### A top-octave deficit on one file `[open]`

The full-corpus spectrum gate's first novel catch. `transcendental.mid` (a chiptune-style
transcription, the corpus's loudest top octave) measures **−9.24 dB in the 11.3–16 kHz band**
against the DLL, while every other band on that file is within 1.1 dB and every other *file*
matches its top octave within 0.9 dB:

| File | ref 16 k band | diff |
| --- | --- | --- |
| canyon | −15.0 dB | −0.00 |
| sc50nn | −16.5 dB | −0.11 |
| bad_apple | −21.6 dB | +0.89 |
| **transcendental** | **−7.8 dB** | **−9.24** |

So the loss only shows where the top octave carries real energy, which is why nothing else caught
it. Both renders were at the hardware's 64 voices with heavy stealing (~2,600 voices), so the
suspects divide into: a stealing-selection difference that preferentially drops high-pitched voices
in the reimplementation, or a genuine high-frequency path difference (interpolator rolloff, a
high-key pitch path, a noise-type partial) that only extreme material excites. Distinguishing them
is cheap: re-render both sides unlimited — if the deficit survives without stealing, it is the
signal path.

### The reverb and chorus macro rows are the GS parameters themselves `[confirmed]`

A GS reverb or chorus **macro** does not select a preset that individual parameter edits then sit on
top of. It loads a row of bytes, and that row *is* the parameter block — the individual addresses
`40 01 31`–`37` and `40 01 39`–`40` write into the same bytes the macro filled in. So a single
parameter edit needs no separate mechanism: overwrite the byte and recompute from the row.

Reverb, 7 bytes from `g_reverb_preset_tbl`:

| Byte | GS address | Parameter |
| --- | --- | --- |
| `[0]` | `40 01 31` | character |
| `[1]` | `40 01 32` | pre-LPF |
| `[2]` | `40 01 33` | **level** |
| `[3]` | `40 01 34` | time |
| `[4]` | `40 01 35` | delay feedback |
| `[5]` | `40 01 36` | (send to chorus) |
| `[6]` | `40 01 37` | pre-delay |

Chorus, 8 bytes:

| Byte | GS address | Parameter |
| --- | --- | --- |
| `[0]` | `40 01 39` | pre-LPF |
| `[1]` | `40 01 3A` | **level** |
| `[2]` | `40 01 3B` | feedback |
| `[3]` | `40 01 3C` | delay |
| `[4]` | `40 01 3D` | rate |
| `[5]` | `40 01 3E` | depth |
| `[6]` | `40 01 3F` | send to reverb |
| `[7]` | `40 01 40` | send to delay |

Note that the coefficient computation reads `[0]`, `[1]`, `[3]`, `[4]` and `[6]` of the reverb row
and **not** `[2]`. Reverb level is not a coefficient — it does not shape the network, it scales what
comes out of it — so an implementation that recomputes the network from an edited row still has to
apply level separately. The same holds for chorus `[1]`.

Why this matters in practice: a file that sets `40 01 33` or `40 01 3A` and is answered with macro
defaults has the wrong wet level for its whole duration. That is a constant offset in the mix, and
it shows up in a render comparison as a **flat** correlation curve with a level error rather than as
anything localised — see COMPARING_RENDERS.md, where a commercial file diverges by 2.50 dB for
exactly this reason.

### The four-band EQ computes nothing `[confirmed]`

`40 02` is not a filter design, it is an index. `fx_eq_band_preset_apply` @ `0x1800407d0` reads two
tables of stored coefficients and writes them straight to registers:

| Band | Table | Index | Registers (L, then R) |
| --- | --- | --- | --- |
| Low | `0x1818960b0` | `freq * 0x4b + (gain - 0x34) * 3` | `0xe7`, `0xe6`, `0xe8` / `0xf4`, `0xf3`, `0xf5` |
| High | `0x1818961e0` | same shape | `0xea`, … / `0xf7`, … |

300 bytes each: 2 corner frequencies × 25 gain settings (`0x34`–`0x4c`, −12…+12 dB) × 3 int16
coefficients, in the same fixed-14 encoding as the reverb and chorus. That is why `40 02 00` and
`40 02 02` accept only 0 or 1 — the corner is a choice of table row, not a parameter. The gain test
is an unsigned `(v - 0x34) < 0x19`, so an out-of-range byte is **ignored**, not clamped.

Each band is a one-pole shelf, `H(z) = (b0 + b1·z⁻¹) / (1 − a1·z⁻¹)`, and the two channels get
identical coefficients — every word is written twice — so the block moves the spectrum and never
the stereo image.

**The flat row is the proof the read order is right.** The registers are written `0xe7`, `0xe6`,
`0xe8` — not ascending — so the natural guess reorders the coefficients. Taken in stored order the
0 dB row is exactly `{1, −a, a}`, which makes numerator and denominator the same polynomial and the
response algebraically unity at every frequency. No other assignment of the three produces that.

Not established: what the printed corner frequencies mean. Reading `a1` as a plain one-pole −3 dB
point gives 225 and 426 Hz for the low band against an advertised 200 and 400 — persuasive — and
then 11 kHz for the high band's second setting against an advertised 6. So that reading is wrong
despite half of it agreeing. `[guess]` — the poles are facts, the Hz are not.

### Part EQ defaults **off**, against the manual `[confirmed — absence of evidence]`

The SC-8820 manual gives `40 4x 20` (EQ ON/OFF, `part+0x450`) a default of `01 ON`. The binary
disagrees: the part reset writes the byte to **zero**, and no code anywhere writes one to it — the
only writer is `sysex_part_param_450` @ `0x180076e90`, the handler for the message itself. A module
never told to switch the EQ on never switches it on.

This is silent until a stream also sends a non-flat `40 02`, because a flat EQ is exactly
transparent. On a stream that sets an EQ curve without addressing `40 4x 20`, the two readings
differ completely.

`[unverified]` against the DLL as an oracle — this rests on an exhaustive search for writers rather
than on a measurement, which is a weaker footing than most findings here. One render of a file that
sets `40 02` and no part EQ would settle it.

### The control matrix's destination scaling `[confirmed — partial]`

`part_mod_depth_recalc` @ `0x180081410` is where the six sources' outputs become one modulation per
destination. For each destination it sums the **five** channel-level source blocks — mod wheel
(`part+0x2a8`), bend (`0x2c0`), channel aftertouch (`0x2d8`), CC1 (`0x370`) and CC2 (`0x388`), each
an 11-short block — stores the raw sum, then clamps and scales it:

| Destination | Clamp | Scale | Result at |
| --- | --- | --- | --- |
| pitch | `0xbe8` (3048) | `(x << 3) * 0xfbf8 >> 16` | `part+0x3ba` |
| TVF cutoff | 4000 | `(x << 3) * 0xc49c >> 16` | `part+0x3bc` |
| amplitude | 4000 | `(x << 4) * 0x820d >> 16` | `part+0x3be` |
| LFO1 rate | 4000 | `(x * 2) * 0xd1b8 >> 16` | `part+0x3c0` |
| LFO2 rate | 4000 | `(x * 2) * 0xd1b8 >> 16` | `part+0x3c8` |
| LFO2 TVA depth | `0xfc0` (4032) | `(x << 4) * 0x8105 >> 16` | `part+0x3cc` |

The two LFO rates share a clamp and a scale, which is what you would expect of the same parameter
on two LFOs, and is a small check that the block offsets were read correctly.

**Polyphonic aftertouch is not in the sum.** Only five blocks are added, and PAf's is not one of
them — it has a separate per-note path, which stands to reason since a channel-level accumulator
cannot hold a per-note value.

Two clamps are worth noting for what they say about units. Pitch's `0xbe8` is 3048, which is exactly
`127 * 24` — the largest `amount * (depth - 0x40)` can reach, so the clamp is the parameter's own
full-scale rather than an arbitrary rail. And the scale then puts full scale at ~24000, fixing the
unit as **milli-semitones**. LFO2 TVA's `0xfc0` is 4032, the same rail `mod_wheel_depth` uses.

`[unverified]` for the four not listed — LFO1 pitch, LFO1 TVF, LFO2 pitch, LFO2 TVF. Their branches
were not read, and guessing them from the pattern is exactly the inference this document keeps
warning about. Anyone wiring them should extract them the same way rather than assuming they follow
the rates.

### Where EQ'd parts go `[confirmed]`

The voice bus-assign sends a part's **dry** signal to bus `0x33` (51) when `part+0x450` is set and
`0x3a` (58) when it is clear. The sends are computed identically either way — only the dry path
detours. The same function sends a part with insertion EFX on (`part+0x452`) to bus `0x3e` (62)
with both send buses forced to the null bus `0x0f`, which is the mechanism behind the manual's note
that system-effect levels become common to all EFX parts.

---

## MIDI → voice pipeline `[confirmed]` (traced end to end)

Traced from the export `TG_ShortMidiIn`. The path is a multi-stage queue → parser → allocator:

1. **`TG_ShortMidiIn`** @ `0x180089370` — decodes the status byte into an internal class code
   (0x90→9 note-on, 0x80→8 note-off, …), timestamps the event, and enqueues it into a
   timestamped **input ring** (`g_midi_in_ring_count`). This function does **no** synthesis — it
   only queues. `[confirmed]`
2. **Scheduler** (inside `TG_Process`) moves events whose timestamp is due this block into a
   **"ready" buffer**. `TG_flushMidi` does the same move unconditionally (used after preset load).
   `[confirmed]`
3. **`midi_drain_ready_to_ports`** @ `0x18008ab90` → **`midi_port_enqueue`** @ `0x180080930`
   pushes each event into a **per-port FIFO** (`DAT_181a22660`, 0xc0-byte queue structs). `[confirmed]`
4. A **table-driven MIDI parser state machine** (`FUN_180072530` and siblings — state handlers
   that return the next-state function pointer) reassembles channel-voice messages from the byte
   stream. `[confirmed]` that it's the parser; `[likely]` on individual state details.
5. **`part_start_voices`** @ `0x180061a40` — walks a part's linked list of active partials/tones
   (list head at part-struct `+0x270`, links at `+0x108`) and starts a voice per partial. `[likely]`
6. **`voice_start`** @ `0x18008f640` — populates the **per-voice parameter arrays** from a tone
   descriptor, then calls the sample setup. `[confirmed]`
7. **`voice_setup_sample_playback`** @ `0x180089b60` — voice index bounded `< 0x40`, so **64
   voices**. Computes the waveform pointer `(key&0x7f)*0x100000 + g_wave_rom_base_a/_b` (two ROM
   banks, selected by `g_voice_wave_ctrl & 0x10`) and loop points. `[confirmed]`
8. `render_block` samplers read those per-voice arrays each block → audio out via `TG_Process`.

### The voice model `[confirmed]`
- **64 voices.** Two representations coexist:
  - `g_voice_run_flags` @ `0x181a1b5b8` — per-voice active flags, **stride 0x50 (80 B)**, bit 0 =
    running. `TG_XPgetCurTotalRunningVoices` just counts these.
  - **Structure-of-Arrays** hot state at `0x181a6f60…0x181a723xx` — many parallel arrays indexed
    by voice (`g_voice_wave_ctrl` etc.). The SoA layout is *why* `render_block` processes voices
    in **groups of 4** (SIMD-friendly), which I noted earlier without knowing the cause.

## Ports: the module has 32 parts, and one AND hides half of them `[confirmed]`

Step 3 above throws away the field that says which port an event was meant for. That single
instruction is the whole reason the module looks like a 16-part device.

**The packet.** `TG_PMidiIn` @ `0x1800892d0` does not take a bare MIDI message — it takes a
**USB-MIDI Event Packet**: `byte0 = (cable << 4) | class`, with the MIDI message in bytes 1–3. The
cable (port) number rides inside every packet. There is no port-select call and nothing latches the
cable between messages, so a host that wants port B sets the nibble on each packet it sends.
`TG_ShortMidiIn` builds the same packet shape with the nibble hardwired to `0`, which is why a host
restricted to that export can only ever reach port A.

**The mask.** `midi_drain_ready_to_ports` @ `0x18008ab90` ANDs byte 0 with `0x0f` before handing the
event to `midi_port_enqueue`, clearing the cable nibble. Every event therefore enqueues onto port A.

**What the nibble would have done.** It is used, in three places, if it survives:

| Consumer | Use |
|---|---|
| `midi_dispatch_flagged_ports` @ `0x180080450`, `port_apply_default_cc_block` @ `0x180080c90` | dispatch through a 16-entry function-pointer table at `port_struct+0x30`, indexed by `byte0 >> 4` — a per-event lookup |
| `midi_event_dispatch_record` @ `0x180080a90` | selects a per-cable parser context at `+0xb8 + cable*0x28`, remaps the cable through the table at `+0x20`, stamps the result into the event's byte 2 → `g_midi_channel` |
| `sysex_key_based_inst_ctrl` @ `0x18007d190`, `sysex_scale_octave_tuning` @ `0x18007d030` | part lookup is 5-bit: `(g_midi_channel & 0xf0) + channel` |

**32 parts, allocated unconditionally.** `g_part_count` @ `0x181a1e704` is initialised to `0x20`, not
`0x10`. The part struct stride is `0x488`, and the second part-array getter @ `0x18005c2c0` returns
`part_base + 0x4880` — exactly sixteen parts on from the first. Nothing is conditional on a model or
a mode flag: the module builds 32 parts every time and then makes half of them unreachable.

SysEx part addressing follows the same rule. `sysex_select_param_map` @ `0x18006b4a0` picks between
the two part arrays on `(g_midi_channel & 0xf0) == 0x00` versus `== 0x10`, so a GS `40 1n` block
address is *port-relative* — it means whichever bank the message arrived on.

**The patch site.** VA `0x18008abf0`, file offset `0x00089ff0`, bytes `41 80 e0 0f` (`and r8b,0Fh`).
Scanning the whole 27 MB image finds exactly one occurrence, so there is no ambiguity about which
instruction is meant.

- [SCWrap](https://github.com/MCModuleStudio/SCWrap) NOPs all four bytes, which uncaps the nibble to
  all 16 cables — four times more ports than there are parts to back them.
- **Tabula Sonora instead widens the immediate to `0x1f`** (file offset `0x00089ff3`, `0f` → `1f`),
  keeping the class nibble plus the low bit of the cable. That admits cables 0 and 1 — the two ports
  the 32 parts actually back — and folds cables 2–15 onto those two by their low bit rather than
  letting them index parts that do not exist.

This is the behaviour both Tabula Sonora engines implement: a port argument on every MIDI entry
point, masked to `0x1f`, with parts addressed as `port × 16 + channel`.

## The sample data (wave ROM) — embedded in SCCore.dll `[confirmed]`

The waveform ROM is **inside `SCCore.dll` itself**, in the `.rdata` section. The DLL is 27 MB on
disk and **96% of it is read-only data**:

| Section | Range | Size | |
| --- | --- | --- | --- |
| `.rdata` | `0x180092000`–`0x181a08bff` | **25.5 MB** | the elephant |
| `.text` (code) | `0x180001000`–`0x180091bff` | 0.6 MB | |
| `.data` | `0x181a09000`–`0x181a75b3f` | 0.4 MB | mutable globals + ROM base pointers |

### Layout within `.rdata` (from a 256 KB-window entropy + int16 profile) `[confirmed]`
- **~24 MB of sample data** up to ~`0x181892000`: uniform **entropy ≈ 7.5–7.8 bits/byte** with
  large int16 deltas. A raw peek at `0x181000000` (`d0 2c 33 13 05 46 7e 4e …`) shows no pointer
  structure — dense signal-like bytes. High entropy + the presence of a **4-bit nibble decoder**
  (`sampler_adpcm4`) means this is **compressed/companded sample data, not plain PCM**. `[likely]`
  on "compressed"; `[confirmed]` it is the sample region and not tables/pointers.
- **~1.5 MB of structured tables** from ~`0x181892000` to the end: entropy drops sharply to
  3–5 bits. This is where the EFX type-map (`0x18189566c`), the **algorithm dispatch table**
  (`0x181895190` — verified: bytes there are exactly `18003d220, 180018070, …`), and the
  `parameter*` tables live. `[confirmed]`
- The **very start** of `.rdata` (`0x180092000`) is itself a small pointer/offset table, so the
  sample bytes begin a little after the section start. `[confirmed]`

### Addressing & init `[confirmed]` / `[likely]`
- Two ROM banks: `g_wave_rom_base_a` (`0x181a18ef0`) and `g_wave_rom_base_b` (`0x181a11a68`).
  `voice_setup_sample_playback` forms a sample address as
  `(key & 0x7f) * 0x100000 | offset + base`, choosing bank B (with a `-0x1000000` bias) when
  `g_voice_wave_ctrl & 0x10`. So the ROM is **banked in 1 MB key-regions**. `[confirmed]`
- The two base pointers are set by `TG_initialize`'s init loop (records 6 & 7 of the blob table at
  `0x181a0fa18` target exactly `0x181a18ef0` and `0x181a11a68`), via their own size functions
  `FUN_1800042a0/b0`. The bases are only ever *read* (no tracked writes) — consistent with this
  indirect init. `[confirmed]` that init sets them; `[guess]` whether the samples are copied to a
  heap buffer vs referenced in place — I did not pin that down.

### Honest limits on the sample-data claims
- I have **not** decoded a single sample to audible PCM. That would require implementing the
  nibble/ADPCM codec *and* locating one specific instrument's start offset + parameters, then
  checking the result sounds right. Everything above is static structure + statistics.
- "Compressed" is inferred from entropy and the existence of the 4-bit decoder, not proven — high
  entropy could also indicate a companded or lightly-obfuscated raw format. `[likely]`, not
  `[confirmed]`.
- Exact sample-region start/end and total sample count are approximate (window-scan resolution).

## Hearing the sauce — real audio + the codec

### The sample codec — REVERSED AND PROVEN `[confirmed]`
Not ADPCM with a step table (my earlier guess) — it's **block-floating-point DPCM** with two
parallel streams. Nailed from `FUN_18003f4e0` (sampler init) + `sample_fetch_loop_wrap`:

```
predictor = 0  (int32)
for each output sample i:
    scale_byte = scaleStream[i >> 5]                       // one byte per 32 samples
    scale      = ((i >> 4) & 1) ? (scale_byte >> 4) : (scale_byte & 0xF)   // nibble per 16-sample block
    delta      = (int8) deltaStream[i]                     // one signed byte per sample
    predictor += delta << (scale + 10)                     // integrate
    output     = predictor * 2^-27                         // 7.450581e-09
```
- **Delta stream** = voice-state `+0x20`, signed bytes, 16-sample blocks.
- **Scale stream** = voice-state `+0x38`, a shift-exponent nibble per block (2 blocks/byte).
- 4-tap FIR (`g_interp_coef_table`) then resamples for pitch. `~8.1 bits/sample` — matches the
  ~7.6-bit `.rdata` entropy.

**Proof (`decoder/` harness):** I loaded the real `SCCore.dll`, played one flute note, read the
live voice's sampler-state struct from process memory (`DAT_181a1b570 + v*0x50`: delta ptr
`+0x20`, scale ptr `+0x38`, len `+0x2c`), and decoded those exact ROM bytes with the code above —
**no engine involvement in the decode**. Result: 6875 samples, peak 8230/32767, and autocorrelation
shows a clean **640 Hz periodic waveform** (period exactly 50 samples). Noise cannot autocorrelate
to a stable period — this is the flute multisample at its native recording pitch.
Output: `flute_sample_ourcodec.wav` (raw) and `..._looped.wav` (sustain tiled to ~2 s).

**Looping & DC:** the predictor is a *pure integrator*, so it accumulates a slow DC drift; the
engine removes it with a DC-blocking high-pass downstream, and loops by **rewinding the delta
index to the loop point while keeping the predictor value** (so the waveform stays continuous at
the seam — you don't stitch absolute chunks). Per-voice loop metadata (position domain, sample
units): `loop_start` (data start, = delta index 0), `end` (loop point, index 2580 for the flute),
`start` (physical end, index 6875). Reproducing that + a ~10 Hz DC blocker gives a steady,
click-free sustained tone. `[confirmed]`

### Generalization — it holds across instruments `[confirmed]`
The loop cosmetics above (pitch-sync loop, DC-block cutoff, crossfade) were **flute-specific tuning
and do not generalize** — the thing that must generalize is the *codec*, and it does. Same decoder,
no per-instrument knobs, six very different instruments captured live and decoded from raw ROM:

| instrument | samples | zero-cross % | f0 | note match |
|---|---|---|---|---|
| church organ | 16400 | 4.1 | 130 Hz | C3=131 ✓ |
| finger bass | 18403 | 1.7 | 131 Hz | multisample root |
| marimba | 2254 | 4.6 | 533 Hz | C5=523 ✓ |
| piano | 59390 | 2.7 | 333 Hz | multisample root |
| strings | 29465 | 9.2 | 376 Hz | — |
| trumpet | 8371 | 9.6 | 352 Hz | F4=349 ✓ |

Noise is ~50% zero-crossings; all six are 1.7–9.6% (strongly periodic) and several land exactly on
the played note. Raw one-shots, no post-processing. (`instr/*.wav`.)

### The second codec variant — reverse playback `[confirmed]`
The other sampler init, `dpcm_voice_init_rev` @ `0x18003ff90` (runflag `0x22`/`0x24`, wave_ctrl
bit 11), is **the same block-FP DPCM run backwards**: identical `predictor += delta << (scale+10)`
accumulation, but position *decrements*, the block index counts `0xf→0`, blocks refill backward,
and the scale byte is read from a *decreasing* index. It pairs with the `_alt` samplers (the
`& 0x20` branch of `voice_render_dispatch`). No standard GM melodic voice uses it; it's for
**reverse-playback SFX** — found at bank MSB=1, programs 119–121 (**Reverse Cymbal** = GM 119).
Extending the decoder to sweep `pos` from `revLen→0` (`revLen = loop_start − (start & ~0x1f)`)
decoded the reverse cymbal correctly — **confirmed by ear as a backwards cymbal**, envelope swells
up like the engine's own render. Both codec variants are now reversed and demonstrated.

**Honest boundary:** the codec is 100% ours and proven. I obtained the sample's *location*
(stream pointers) by reading the running engine's voice state — ground truth — **not** by fully
statically reversing the tone→wave directory (`.tnf`/`.drk` + `.rdata` tables). So a pure-offline
extractor (decode any instrument without running the DLL) still needs that directory reversed;
everything downstream of "given a sample's coordinates" is done.

### What I actually produced — audio through the real engine `[confirmed]`
To *hear* it now (and to validate the whole reconstruction end to end), I drove the real
`SCCore.dll` via the sibling `SauceForYourEars` CLI (dotnet 10):
- `flute_note.wav` — one sustained note, **peak voices 1** = a single looped ROM sample in
  isolation. The cleanest "one decoded sample" I can show without the directory reverse.
- `piano_arp.wav` — arpeggio + chord, **peak voices 7**.
- Rendered dry/full-level (MIDI CC7=127, CC10=64, CC91=0, CC93=0; velocity 127).

**Honest framing:** these are decoded by the engine's own codec, not by a decoder I wrote. That
the engine renders correctly and reports exactly the voice counts my `render_block`/voice-alloc
analysis predicted (1 and 7) is strong end-to-end validation of the static RE — but it is *not*
the same as independently decoding the ROM. The independent static decode remains the open
frontier below.

## Fully-static wave ripper — the whole ROM `[confirmed]`

The ROM is stored **verbatim** in `SCCore.dll`'s `.rdata` (two banks). Bank base file offsets,
pinned by byte-identical validation against the live engine:
- **bank A: `0x92700`** · **bank B: `0x1092730`**

A wave's streams (given `{region, bank, reverse, loop_start, end, start}`):
```
eff_region = region - 16*bank
base       = bankbase + eff_region*0x100000
delta      = base + (loop_start & ~0x1f)          # 1 signed byte/sample
scale      = base + ((loop_start & ~0x1f) >> 5)   # nibble per 16-sample block
n          = reverse ? loop_start-aligned : start-aligned
```
then the block-FP DPCM codec. Both banks and both directions validated **byte-identical** (max
diff 0 LSB) to the engine.

**Wave directory:** 1868 unique waves across 24 regions (`wave_directory.csv`), enumerated by
sweeping bank/program/note through the engine and reading the per-voice arrays. Categories:
1188 fwd/bankA, 638 fwd/bankB, 14 rev/bankA, 28 rev/bankB. Waves are **packed sequentially within
a region** (`wave[i+1].start ≈ align32(wave[i].end)`), which is why the absolute positions aren't
stored as literals — they're accumulated at init.

**Result:** `rip_waves.py` ripped **1859/1868 waves** straight from the DLL file (no engine in the
extraction path) — ~482 s of raw samples, 94% clearly tonal. This is the complete SC-VA wave ROM.

**Honest boundary:** the *extraction* is fully static and exact. The *directory* (which
`{region,loop,end,start}` each wave has) was enumerated via the engine, not parsed from a static
table — those positions are computed at init from sequential packing, so a no-engine directory
would require reversing the init accumulation + the program/note→wave map. The samples themselves
are fully ripped either way.

### Multisamples — key zones + velocity layers `[confirmed]`
An instrument is a **multisample**, not one wave. Mapping a program across the whole keyboard ×
velocity (`map` mode) shows it directly. Example — GM Piano (prog 0):
- **Key zones:** the wave switches ~12 times across the keyboard (notes 0–31 → one wave, 33–41 →
  another, 42–46 → another, …); within a zone the same wave is pitch-shifted.
- **Velocity layers:** at many keys the wave switches by velocity — e.g. note 68 has **three**
  velocity layers (soft/mid/hard = different waves).

Consequence: the first enumeration (fixed velocity) **undercounted**. Sweeping velocity found
**2089 unique waves** total (+221 velocity-layer waves the single-velocity pass missed).
`rip_waves.py` on the merged `wave_directory_full.csv` rips **2072/2089**. Still missing for a
*playable* multisample: per-zone `{key_lo, key_hi, vel_lo, vel_hi, root_key, fine_tune}` — the
`map` mode captures the key/velocity boundaries; root key/tuning would come from the descriptor
tuning fields (voice_start reads pitch fields at descriptor +0x98/+0x9c).

## Names — not in the DLL, in the companion files `[confirmed]`

The DLL carries **no readable bank/instrument names** (string hits in it are byte-coincidences in
the sample data). Names live in the plugin's **`.tnf`/`.drk`/`.drf` "SSW TONEFILE" files**:
- Module/bank names (`MODULENAME=`): **GM**, **GM2**, **SC-8820**.
- `SCVSC.tnf` holds **5 tone maps**: `0 Default`, `1 55Map` (SC-55), `2 88Map` (SC-88),
  `3 88ProMap` (SC-88Pro), `4 8820Map` (SC-8820) — matching the plugin's vintage-map selector.
- Full tone-name lists and drum-key names (`[88] Standard 1 Kick 1`, etc.) are in these files.
These enable naming the ripped waves (tone name → program → enumerated wave).

## The static patch directory — the whole thing, no engine `[confirmed]`

After a long detour querying the live engine to build the multisample map (which was
contamination-prone — stale/phantom voices mislabeled zones; the *samples* always decoded
correctly, only the *labeling* was wrong), the correct path was to reverse the **static patch
tables** the engine reads at init. These are the 8 init-copied blobs from `TG_initialize`; their
`.rdata` sources are readable directly. Every layer below is validated **byte-exact** against the
engine.

**Wave descriptor table** — `.rdata 0x181897b40`, stride **0x16** (decoded by `wavedesc_decode`
@0x18005ec90). Per wave: `region = byte[0]&0x7f`; `loop = (b[1]&0xf)<<16|b[2]<<8|b[3]`;
`end` = same from bytes 7-9; `start` from bytes 0xb-0xd (all 20-bit region-relative, stored
directly — NOT accumulated); `root key = byte[6]`; `fine = u16 at [4]`; `flags[0xa]` bit1=loop,
bit2=reverse; ROM bank = `(region>>4)&1`. Validated: flute = wave#806 → region 6, loop 800928,
end 803508, start 807803, root 75 — exact match to the captured coordinates.

**Multisample table** — `.rdata 0x1818ca570`, stride **0x8c** (`multisample_select_wave`
@0x180003420). Key-split upper bounds at `+0x0c` (walk `while key>bound`), primary wave# `s16` at
`+0x2c[zone]`, velocity-layer alternates at `+0x2a`/`+0x2e`, fallback `+0x6a`. Key is transposed:
`key = note + (0x40 − partial.keyCenter)`. Validated: flute multisample #111 = ascending waves
797→809.

**Tone table** — melodic `.rdata 0x1818f2810`, stride **0x100** (`tone_lookup` @0x1800026d0);
= 0x24 header + **2 partial-param blocks × 0x6e**. **The tone NAME is ASCII in header[0..11]**
(matches SCVSC.tnf 8820Map exactly: tone#0 "Piano 1", #39 "Harpsichord", #71 "Marimba"). Each
partial: multisample idx `+2` (0xffff = none), key center `+4`, velocity range `+0x4f`/`+0x51`.

**program/bank/map → tone#** — reversed from `FUN_180069200`, a 3-level nested LUT:
`tone# = s16(DAT_1819f32b0[ DAT_1819f28b0[ DAT_1819f2e30[map]·0x80 + bank ]·0x80 + program ])`,
with `map`: 1=SC55, 2=SC88, 3=SC88Pro, 4=SC8820. Validated **25/25** vs engine, and it's
vintage-accurate (Piano 1's sample changes SC55 `r4` → SC88 `r5` → SC88Pro/8820 `r8`).

**Result — `scvx_directory.py`** resolves `(map, bank, program, note, velocity) → wave(s) + ROM
coordinates` with **zero engine involvement**, byte-exact. **`rip_static.py`** rips all 1024 tones
into `rip_static/<tone#>-<name>/<velLo>V<velHi>-<keyLo>K<keyHi>-P<partial>-...wav` — clean,
correctly labeled, no possible stale-voice contamination. Tables exported to `tables/*.bin`.

The full static chain: `(map,bank,prog,note,vel) → LUT → tone# → tone table → partials →
multisample → key/vel zone → wave# → wave descriptor → ROM coords → block-FP DPCM codec → PCM`.
This is the complete **data + directory layer** of an embeddable engine, self-describing (names,
root keys, tunings all from the ROM) and vintage-selectable.

## The synth back half — per-partial TVA / TVF / pitch env `[confirmed map, uncalibrated units]`

The "front half" (which sample sounds) was the directory. The "back half" is *how* that sample is
shaped: amplitude envelope (TVA), filter (TVF), pitch envelope, LFO. **All of it lives in the same
0x6e partial-param block I already exported in `tone_a.bin`** — no new ROM structure. The engine
reads that block through `(*DAT_181a74920)()`; every offset those compute functions touch is a
static field. So the synth params are extractable statically, exactly like the wave directory.

**Key realization:** `DAT_181a74920`/`DAT_181a74918` return the work buffer that `partial_load_params`
fills from `*(voice+0x148)` — the partial's 0x6e block (tone-table stride 0x6e, max offset read 0x6b).
Block-relative offset == tone-table-block offset. `scvx_partials.py` decodes them.

**0x6e block field map** (offsets relative to block base = `tone+0x24+i*0x6e`; `[c]`=read by a compute
fn, cross-checked against physically-sensible values across many instruments):
- **Pitch** (`partial_compute_pitch` @18005fc20): `+0x11` coarse `(v-0x40)*10`; `+0x12` bend/LFO depth.
- **Pitch env** (`partial_compute_pitch_env` @18005fde0): `+0x18` depth; `+0x1b..+0x1f` 5 stage
  biases (each `-0x40`); rate key-follow `+0x27/+0x29/+0x2a`, vel `+0x2c`.
- **TVF** (`partial_compute_filter` @180061210): `+0x2f` cutoff base (`×0x100 → voice+0x1f0`);
  `+0x30` cutoff key-bias; `+0x31` filter **TYPE** (0/1/2/4/5/6 else bypass → `g_filter_type_coef`);
  `+0x32` env-depth key-follow (low nibble); `+0x33` env depth (0x40 center); `+0x3a..+0x3e` 5 env
  levels (via `tvf_env_level_conv`); `+0x46` env-rate key-follow; `+0x4a` resonance (0x40 center).
- **TVA** (`tva_compute_base_level`@180060960, `tva_compute_env_levels`@180060b00): `+0x53` base
  level; `+0x54/+0x55` level key-follow; `+0x5a..+0x5d` 4 env-stage levels; `+0x65/+0x66` rate
  key-follow rows; `+0x67/+0x68` rates; `+0x69/+0x6a` rate vel-sens; `+0x6b` level key-follow 2.
- Velocity gate `+0x4f`/`+0x51`, patch level `+0x50` (already used by the directory).

**Shared converters** (renamed): `env_rate_scale`@1800607e0 — `(baseRate, 0x40-neutral param) →`
8.8-fixed rate multiplier via `g_env_scale_curve` + `g_env_rate_out`; returns `0x100` (=1.0) at
neutral. `env_level_scale`@180060880 — same family for depths. `lfo_value`@18008fbb0 — LFO output.

**Curve tables exported** (`tables/curve_*.bin`, `tables/kf_*.bin`) and labeled in Ghidra:
`g_env_rate_out` @1819a3060 is a **`2^(x/8)` exponential** rate table (`[0x80]=0x100` exactly);
`g_level_curve` @1819a2a00 is a monotonic dB-style level→attenuation map (idx0=silence, 127=full);
`g_filter_type_coef` @181987b00 (7×4B, type0=passthrough); `g_reso_curve` @1819a2b88 (recheck idx8);
plus the 2D key-follow tables `g_kf_pitch/tvfenv/tvalevel/tvarate0/tvarate1/tvfrate/pitchrate0/1`.

**Validation** (static, cross-instrument — no engine call): "Cho. E.Piano" partials carry a symmetric
**±3 coarse detune** (`0x3d`/`0x43`) — that's how you build a chorused EP; Piano vs "Mild Piano" reuse
the same multisamples with a hard/soft **velocity split** (80–127 / 0–79) differing only in cutoff;
every instrument's TVF env levels form a **monotonic decay** ([127,124,100,81,64] piano). The field
map is confirmed; what remains is **calibrating raw bytes → physical units** (rate→ms, cutoff→Hz).

**Tools:** `scvx_partials.py` (`partial_params(tone#, partial)` → named dict; `dump(tone#)`).
12 back-half functions + 17 curve-table labels committed to the Ghidra project (`RenameSynth`).

### Envelope generator — the timing model `[reversed; one scalar uncalibrated]`

Traced the actual envelope engine, not just the setup. Chain (all renamed in the project):
`control_tick_dispatch`@18008f0d0 → `voices_control_update`@1800849a0 (iterates **64 voices**,
stride 0x220) → `voice_block_process`@180080e40 (snapshots voice → scratch, runs the stages, writes
back) → `env_ramp_segment`@180083a70. The internal engine runs at a **fixed 32000 Hz** (`TG_setSampleRate`
sets the host rate; the internal render clamps to 32000).

`env_ramp_segment` is a **16-bit phase-accumulator** ramp on the env-state block (voice+0xc):
`+0x06` rate, `+0x08`/`+0x0a` segment start/target level, `+0x0c` current output, `+0x0e` phase.
Per control tick: `phase += rate × (g_env_block_speed + carry)` (`g_env_block_speed`@181a2283c is
normally 1; a second pass uses a sub-rate). When `phase` wraps past `0xffff` the **segment completes**
(output snaps to target, next segment loads); otherwise output **interpolates** start→target by phase.
So a segment lasts **`0x10000 / (rate × speed)` control ticks**.

The **rate** itself = base step × `env_rate_scale`, and `env_rate_scale` = `g_env_rate_out[i]` =
**`2^((i−0x80)/32)`** exactly (verified numerically: `[0x40]=2⁻²`, `[0x80]=2⁰`, `[0xa0]=2¹`, `[0xc0]=2²`).
The index `i` is built from the block's rate byte + key-follow (`g_kf_tvarate*`) + velocity, all
0x40-centered, so each stage's speed is a clean power-of-two-per-32-steps modulation of a base.

**Absolute time — CALIBRATED empirically `[confirmed]`.** `t_segment = (0x10000 / (rate × speed)) ×
Δt_tick`. The scalar was measured by reading the live env-state (`rate`@struct+0x12, `phase`@+0x1a,
`cur`@+0x18) from the voice-control array (accessor `DAT_181a749e0(0)`, stride 0x220) across small
render chunks at Fs=32000 (`scdec calib`). Result: on the strings attack (`rate=13107`, `speed=1`)
the phase stepped by **exactly 13107 (= rate) every 320 internal samples** — four consecutive 320-sample
intervals — so **`control_block_samples = 320`, control rate = 32000/320 = 100 Hz, `Δt_tick = 10 ms`**.
The segment completed in `65536/13107 = 5.0` ticks = **50 ms**, with `cur` reaching `tgt` exactly as
`phase` hit `0xffff` — model and engine agree to the integer. (Confirms `render_block` = 32 samples,
control update every 10 blocks = the `+=1000/wrap@10000` divider.) **The full law:
`t_segment = (0x10000 / (rate × speed)) × 10 ms`** (speed normally 1). Level side solved too:
`g_level_curve` (stage level → 16-bit log) + `g_amp_curve_hi/lo` (16-bit log → linear, `0xffff`→0 dB,
`0x8000`→−42 dB).

4 envelope-engine functions + 3 labels committed (`RenameEnvEngine`). Calibration harness: `scdec calib
<prog> <note> <vel> <framesPerStep> <steps>`.

### TVF filter — cutoff → Hz `[SUPERSEDED — see the engine section below]`

> **Correction.** The law derived here uses `voice+0xc8` and was fit from the *saw* patch only.
> It does **not** transfer across patches (the piano's real cutoff is ~2.9× what it predicts).
> The runtime cutoff field is actually **`voice+0xcc`**, which yields a *universal* law —
> see "The playable engine" section. The `+0xc8` fit below is kept for history.


Measured empirically (`scdec filt`): play a bright saw (prog 81), sweep CC74 (brightness → TVF cutoff),
capture steady-state PCM + the live voice-control struct at each setting. Dividing each output spectrum
by the wide-open reference gives the filter's magnitude response; fitting that to a lowpass shape shows
a **2-pole (−12 dB/oct) lowpass** decisively beats 1-pole (fit residual ~2× lower). The runtime cutoff
is the u32 at **`voice+0xc8`** (max `245760 = 0x3C000` = fully open), *not* `+0x1f0` (that's the static
base `block[0x2f]×0x100`; CC74 / key / env fold into `+0xc8`). Fitting the fitted corner `Fc` against
`+0xc8` over 0.4–5.6 kHz:

> **`Fc ≈ 17640 × 2^((C − 245760) / 14273)`**   (C = `voice+0xc8`) — a **log-frequency** cutoff,
> ≈ **14300 units/octave** (~1190/semitone), full-open ≈ 18 kHz.

Max residual 7.6% (a quadratic fit tightens it to ~2% — the law has mild curvature). Data points
(C → Fc): 220524→5.6k, 209672→2.95k, 198780→1.70k, 187868→1.02k, 176948→623, 166028→388 Hz. Harness:
`scdec filt <prog> <note> <outdir>`; analysis `filt3.py` (needs the scratchpad venv/numpy).

### LFO / vibrato — fully reversed + calibrated `[confirmed]`

The LFO reuses the **same 100 Hz phase-accumulator** as the envelopes (`lfo_advance_waveform`@180082a30:
`g_lfo_phase += rate`, evaluate waveform → `g_lfo_out`; `lfo_update`@180081b90 runs it per tick and
writes the per-voice mod array `DAT_181a227d8[v*6] = {pitch,TVF,TVA}` that voices read via `voice+0x170/
0x180/0x198`). Note: the function I'd earlier tagged `lfo_value`@18008fbb0 is actually a **Galois LFSR
PRNG** (renamed `prng_lfsr`) — it feeds the *random* LFO shapes, it's not the oscillator.

- **Rate → Hz** (`f = rate × 100 / 65536`, `rate = g_lfo_rate_tbl[param]` @1819a2790): **0–20 Hz**, a
  clean **0.1 Hz per param unit** to ~8 Hz then accelerating (param 16→1.6, 32→3.2, 50→5.0, 80→8.0,
  127→20 Hz). The round 0.1 Hz steps **independently confirm the 100 Hz control tick.**
- **Waveforms** (`g_lfo_waveform_sel` @181a227d0): 0 = **sine** (`g_lfo_wave_tbl` half-sine, fit residual
  0.6%, mirrored by sign logic), 1 = random S&H (`prng_lfsr`), 2/3 = slewed random (±0x50/tick), 4 =
  square, 5 = sawtooth, 6 = triangle.
- **Depths**: pitch via `g_lfo_cents_tbl` @1819a2690 = **10 cents/unit** (0.1 semitone), up to ±6000
  cents; TVF/TVA depths scale similarly. Mod-wheel (CC1) adds on top of the part's vibrato params
  (`part+0x3a8..0x3ae`), so `effective_depth = base + CC1·sens`.

Exported: `tables/lfo_wave_1740.bin`, `lfo_rate_2790.bin`, `lfo_cents_2690.bin`. 5 LFO functions + 7
labels committed (`RenameLfo`).

## The playable engine — `scvx_engine.py`, A/B'd vs real audio `[validated]`

The reversal is now a **working synth voice with SCCore.dll NOT loaded at render time**. Chain, all
from static ROM tables: `(prog,note,vel,map) → program_to_tone LUT → tone/partial → multisample
key/vel zone → wave# → descriptor → block-FP DPCM codec (numpy cumsum) → pitched loop playback →
TVF 2-pole LP → TVA envelope → mix → WAV`. `render_song(events)`. Validated by rendering the *same*
note sequence through the real DLL (`tools/decoder`, `scdec song`) and comparing spectra/contours.

What is now **fully static, no fudge constants**:
- **Pitch**: `native = root + (1024 − fine)/1000`; ratio `2^((note − native)/12)`. Verified to ~0.5%.
- **Codec + loop**: loop the sustain region `[end,start]` whenever it exists (`n − loopS > 64`) — the
  descriptor flag bit does *not* gate looping; without it, pitched-up notes run out one-shot and cut off.
- **TVF cutoff → Hz — UNIVERSAL law** (replaced the earlier saw-only law + `×2.15`/`×0.5` fudges): the
  runtime cutoff field is **`voice+0xcc`** (not `+0xc8`, which fit the saw only by coincidence).
  `+0xcc = block[0x2f]×633.5 + 176882`; **`Fc = 10591 × 2^((C − 245760)/14175)`** — fits piano AND saw
  to ~5%. Cutoff is **note-independent** (no key-follow; brightness stays flat via the multisample).
- **TVA envelope — fully reversed segment machine** (`tva_compute_env_rates`): 4 segments (levels
  `block[0x5a..0x5d]`, rates `block[0x5e..0x61]`) + release (`block[0x62]`). `segment_time_ms =
  (vel_mult × min(0xffff, (rate_mult × g_rate_curve[rate_byte])>>8))>>8`, with mults from
  `env_rate_scale`/`env_level_scale`. Interpolation via **`g_env_shape`** (fast-approach curve,
  `tables/env_shape_7a90.bin`) in the gain domain. Validated: attack 0 ms, hold 626 ms, decay 16.8 s
  (matches the engine's live `cur` to ~2%).

**Two real bugs found via the A/B** (both would have shipped silently):
- *Looping off* — trusted `flags & 2`, which reads 0 for piano, so samples one-shot and the top chord
  note vanished at note-off. Fix: loop when a sustain region exists.
- *TVA base-level* — baked `block[0x50]` (partial-level attenuation, =10 for SC-55) into `base16`,
  crushing the envelope targets into the `amp_of` floor → decay plateaued. Fix: `base16 = 0xffff·vel/127`
  only. SC-8820 (`block[0x50]=80`) masked it.

**Vintage-selectable, validated on both maps**: same program at `tone_map=1` (SC-55) vs `4` (SC-8820)
pulls era-correct ROM samples (different tone#, region, root); pitch, brightness, decay, and release
all A/B cleanly against the real engine on each. Tooling: `tools/decoder` (`scdec` — modes
`calib`/`filt`/`lfo`/`song`/`voices`/`map`), `tools/ghidra_scripts`, `tools/analysis`.

## Drums — the kit table `[confirmed]`

Drum kits are a **static note-indexed table at DLL file offset `0x18AD950`** (stride `0x400`), parallel
planes: `+0x000` tone# (128×u16), `+0x100` level, `+0x180` coarse pitch (60 = natural), `+0x200`
mute/assign group (hi-hats share 1), `+0x280` pan, `+0x300` reverb/flags. Dumping it yields the GM
Standard kit by name (BsDrum1/2, Side Stick, Snare, HandClap, HiHats, Real Toms, Crash, Ride, China,
Ride Bell, Tambourine, Splash, Cowbell, Vibraslap).

Three non-obvious facts, each verified against the engine:
- Drum sounds are ordinary **melodic-table tones** (tone# < 0x4000). The drum table (≥0x4000, stride
  `0x1e8`) is *not* what GM kits use.
- Drums do **not** resolve through the 3-level LUT — checked live and statically (it returns
  "Slap Bass 1" for a kick).
- The note does **not** transpose the sample. Resolve at key 60, pitch from the wave root, then apply
  the `+0x180` coarse offset at **half strength — `2^(offset/24)`**. Measured: notes sharing a tone
  (41/43, 45/47) come out `2^(off/24)` apart. Drums also **ring out** (note-off ignored).

**Loop-enable rule (corrected):** wavedesc `flags` **bit1 (0x02) set ⇒ one-shot** (those waves have a
zero loop region); clear + a real region ⇒ sustain loop. Piano = 0x00 (loops); kick/snare/hat = 0x02
(one-shot, decay naturally); toms/crash/snare-rolls loop.

**Per-partial pitch fields that were never applied:** `block[0x10]` = key transpose (0x40 neutral,
whole semitones), `block[0x11]` = coarse tune (`(v-0x40)*10` milli-semitone). Neutral on piano so
invisible there, but tone#1782 has `0x10=61` (−3 semitones). Applying both made all five tom pitches
match real exactly (147/127/107/107/87 Hz).

## TVF cutoff envelope `[implemented, partly validated]`

`tvf_env_level_conv`@180061640: `off = lvl−0x40`; `delta = (((block[0x38] × c0) & 0x7fffffff)>>15) ×
DAT_1819a2890[|off|] >> 7`; result `= envPeak ± delta`. `envPeak` comes from depth `block[0x33]` via
`g_kf_tvfenv[(block[0x32]&0xf)*0x80+key]` and the depth curves at `0x1819a2fa8`/`0x1819a3028`.
Structure mirrors the TVA: **4 segments (`block[0x3a..0x3d]`) + release (`0x3e`)**, rates
`0x3f..0x42` + `0x43`. Levels are stored **relative to peak**, so `cutoff_byte(t) = block[0x2f] +
offset/256` → the universal Fc law. The env **starts at its rest/release level** and attacks to
stage 0 — that is what makes a sweep pad open from dark. Applied via a block-wise biquad carrying
filter state (`apply_tvf_varying`).

**Known-wrong / open (next session):**
- **`block[0x4a]` is NOT resonance** — mislabeled. Sweep Pad has `0x4a=64` (neutral) yet audibly
  resonates; it actually feeds `DAT_181a1f5c0`, which scales the *envelope depth*. The real resonance
  source is unknown (candidates: `block[0x30]`, `g_filter_type_coef[block[0x31]]`). The `Q =
  0.707·2^((v−64)/18)` in `apply_tvf` is **invented** and should be removed.
- **TVF sweep ~5× too fast**: `FUN_1800616f0` uses start-phase table **`DAT_1819a7a00`**, not the
  TVA's `DAT_1819a7a30` — the TVF env likely has its own interpolation shape to export. Real sweep pad
  centroid 387→1409 over 1.5 s; ours saturates by 0.3 s (endpoint matches: 1493 vs 1409).
- ~~**Loop seam**: … a short loop crossfade would help.~~ **Resolved — there was no seam bug, and a
  crossfade would have been an invention.** See "Loop seam — investigated and closed" below.
- Drum attack "punch" still short (high-frequency 0.20–0.30 vs real 0.57); expected to improve once
  the TVF env shape and resonance are correct.

## Loop seam — investigated and closed `[confirmed]`

I had logged the sweep pad's 399 ms whole-sample loop as a "seam" defect and proposed a crossfade.
Both halves of that were wrong. What the sampler actually does:

**Three sampler variants**, chosen by `voice_render_dispatch` @`18003f720` on voice byte `+0x48`
(`bits1-2`; note this is *not* the wave-descriptor `flags` byte — the empirical descriptor rule
still stands):
| `+0x48 & 6` | function | behaviour at end of data |
|---|---|---|
| 0 | `sampler_pcm` @`18003f9d0` | one-shot: raises the voice-done flag and **zeroes the predictor** |
| 2 | `sampler_adpcm4` @`18003fb80` | **loops**: `index := *(param_1[2]+0xc)` (the loop point), reloads the scale nibble + 16-byte delta window, and **leaves the predictor untouched** |
| 4 | `sampler_fmt4` @`18003fdd0` | bidirectional — flips a direction bit and walks backwards via `FUN_18003f920` |

So the engine loops **in the delta domain**: it rewinds the delta/scale index and keeps
integrating. There is **no crossfade** anywhere in the sampler, and the seam is therefore never a
discontinuity — the first sample after the wrap is just `predictor + d[loopStart]`, one ordinary
delta step (sweep pad P1: 1.4× the mean |step| inside the loop — inaudible).

**Loop points are correct.** Cross-checked the static descriptor against the values read live from
the engine's own sampler state for the flute: wave#806 → loop point index 2580, length 6875. Exact
match. The pad's loop really is `[48, 12826]`.

**The consequence I had not seen:** the predictor is a *pure integrator with no leak*, so carrying
it across the wrap adds a constant DC step `drift = buf[loopE-1] - buf[loopS-1]` on **every pass**.
Measured over all 2481 looped waves: median `0.16 × rms` per pass, p99 `2.2 ×`; per second the
median is `3.8 × rms/s` and the flute is `-4.3 × rms/s`. That cannot survive more than ~a second,
so **a DC blocker must exist downstream and we have not located it** — the per-voice chain
(`FUN_18008d9a0`) is a plain lowpass with unity DC gain, so it is elsewhere (bus mix or effects).

**What `scvx_engine.play_wave` does, and why it is defensible:** it re-stitches the decoded region
absolutely. That is *identical* to the engine's loop with the per-pass DC removed exactly — the
ideal-DC-blocker limit — differing only in the seam step (`buf[loopS]-buf[loopE-1]` instead of
`d[loopS]`). Implementing the raw delta carry without the real blocker would make output strictly
worse, and picking a cutoff by ear is exactly the empirical tuning we ruled out. Fixed for real:
interpolation across the seam now reads into `buf[loopS]` instead of running off the buffer end.

**Postscript — the pad never used this sampler at all.** Everything above is correct for
`sampler_adpcm4`, but the sweep pad's waves are dispatched to `sampler_fmt4`. See the next section;
the audible bump was real and is now gone.

## Sampler variant selection — decoded end to end `[confirmed]`

`wavedesc_decode` @`18005ec90` packs the descriptor flags into `wave_ctrl`, voice-start unpacks
them, and `voice_render_dispatch` @`18003f720` switches on the result:

```c
wave_ctrl = ((flags & 4) + ((flags & 1) + 8) * 8) * 0x200 + region;   // wavedesc_decode
u33 = (wave_ctrl >> 2 & 0x400) | (wave_ctrl & 0x800);  u34 = u33 >> 10;
run_flags = (u33 == 0) ? 0x02 : (u34 == 1) ? 0x04 : (u34 == 2) ? 0x22 : 0x24;
switch (run_flags & 6) { 0: sampler_pcm; 2: sampler_adpcm4; 4: sampler_fmt4; }  // +0x20 -> _alt
```

| descriptor `flags` | count in ROM | sampler | behaviour |
|---|---|---|---|
| 0 | 2756 | `sampler_adpcm4` | forward loop `[loopPoint, dataEnd]` |
| 1 | 612 | **`sampler_fmt4`** | **bidirectional / ping-pong** |
| 2 | 649 | `sampler_adpcm4` | **all 649 have an EMPTY loop region** (loopPoint == dataEnd) ⇒ one-shot |
| 6 | 79 | `adpcm4` + `_alt` | reverse (`dpcm_voice_init_rev`) |

So **bit0 = bidirectional, bit2 = reverse, and bit1 plays no part in the dispatch.** Our old
`flags & 2 ⇒ one-shot` rule was right by accident — those waves are one-shots because their loop
region is empty, which the `n - loopS > 0` test already catches.

**`sampler_fmt4` in detail** — it walks the delta index up to `dataEnd`, turns around, walks back
down to the loop point, turns around again, forever. On a turnaround step the index is *not*
changed, so that sample's delta is applied twice. Crucially the predictor keeps **accumulating in
both directions** (`FUN_18003f920` decrements the index but the caller still does `predictor +=
delta[index]`), so the backward leg is the wave *inverted and time-reversed* — and both turnarounds
are continuous by construction. **There is no seam and no phase jump.**

Halo Pad's two partials are both `flags = 1`. Implementing the ping-pong removed the bump outright:
instantaneous-frequency stability of the raw pitched stream went from **11.73 Hz σ → 1.36 Hz** (P1)
and **18.49 → 9.71 Hz** (P0), and the repeatable per-wrap excursion (2.4-2.5× baseline, present at
every wrap) is gone because there is no longer a wrap.

## TVA rate byte bit 7 = ramp SHAPE, not rate `[confirmed]`

`tva_compute_env_rates` @`0x180060ca0` reads each rate byte **twice**: once signed, to extract a
shape word, and once masked, for the table index.

```c
shape = ((int16)(int8)B) >> 15 & 0x4000;     // bit7 set -> 0x4000, else 0
idx   = clamp((B & 0x7f) + bias, .., 0x7f);  // bias = (pch[0x457+k] + pch[0x3e8+k])*2 - 0x100
T     = g_rate_curve[idx];  if (T < 9) T = 0;
```

and `env_ramp_segment` @`0x180083a70` branches on it:
- `0x4000` → **linear**: `out = start + ((target-start) * phase) >> 16`
- `0` → **exponential**: the `g_env_shape` fast-approach curve

`g_env_shape` is ~99.6% of the way to target by half the segment, so rendering a *linear* segment
with it collapses that segment into a near-instant jump — which is exactly what we were doing to
every segment. Halo Pad partial 1's rate bytes are `88 D2 2C 3D` — **segments 0 and 1 have bit 7
set**, i.e. its entire fade-in is the two linear segments.

**The TVF and pitch envelopes hardcode `0x4000`** (`FUN_1800616f0` writes the literal at every
stage), so those are *always* linear — we were applying the exponential curve there too, which is
the likely cause of the "TVF sweep ~5× too fast" symptom. Level bytes `+0x5a..0x5d` have no bit-7
question: 0 of 16384 level bytes in the ROM have it set, versus 5910 rate bytes that do.

All four corrections are in `scvx_engine.py` (`decode_wave`/`play_wave`, `_seg_curve`, `_seg_ms`,
`compute_tvf_env`).

## Roland's DC blocker — found; placement on the dry path NOT confirmed `[confirmed design, unconfirmed placement]`

There is exactly one DC-blocker design in the binary, instantiated three times — and all three sit
on **effect-processor inputs**, none on the dry voice path:

| function | address | state | input |
|---|---|---|---|
| `FUN_1800851c0` | `0x1800851c0` | `DAT_181a62ae8` | delay/chorus-1 input (bus 2) |
| `FUN_180085460` | `0x180085460` | `DAT_181a629e0` | chorus send (bus 3), 4× unrolled, shared state |
| `FUN_180086140` | `0x180086140` | `DAT_181a62aa0` | reverb send (bus 60) |

```c
static float dc_state;
float dc_block(float x) {              /* verbatim, immediate float literals */
    float y  = x * 0.99804f + dc_state;
    dc_state = dc_state - 0.003919f * y;
    return y;
}
```
`H(z) = b0(1 − z⁻¹)/(1 − Rz⁻¹)`, pole `R = 1 − k = 0.996081` ⇒ **fc = 19.9985 Hz at 32 kHz**, with
`k = 1 − e^(−2π·20/32000)` and `b0 = (1+R)/2` — a textbook 20 Hz one-pole. Use this verbatim when
we implement the effects. `0.99804` is the **only** float literal in the whole 84 k-line decompile
in `(0.85, 1.0)`, so no second blocker design exists.

**The dry path was walked end to end and has none**: `render_block` → `voice_render_dispatch` →
per-voice SVF (`FUN_18008d9a0`, unity DC) → `FUN_18008af50` (pure MAC into 4 buses; dry = buses
58/59) → `fx_process_block`'s 33-bus send matrix (*not* an FIR — memoryless) → `FUN_18008bd30`
(sums dry bus 58 unfiltered) → `tg_output_filter` @`0x18008aca0`, which is a **first-order allpass**
`(k + z⁻¹)/(1 + kz⁻¹)` used as a half-sample delay for the 2× interpolating SRC — unity at DC.

The removal demonstrably happens anyway: measured on the real dry renders, max |DC| in 50 ms windows
is 0.0045 (`real_sweeppad.wav`), 0.0040 (`real_engine_piano.wav`), 0.0007 (`flute_note.wav`, vs a
0.159 peak) — where an unblocked flute would pass full-scale DC in ~2.5 s. Note the flute figure is
~5× *lower* than a 20 Hz one-pole would settle at (`drift_rate/2π·fc` ≈ 0.0033), which argues the
dry-path mechanism is **not** simply this filter. Three possibilities remain, unsettled: the blocker
lives in the host wrapper outside `SCCore.dll`; it is in a mis-decompiled region (the bus-clear loop
at `render_block` 78210–78232 provably is — as printed it would wipe dry bus 58 before
`fx_process_block` reads it); or a routing detail is misread.

Also re-confirmed along the way: the drift is real and the encoder does not cancel it. Flute
wave#806 `pred[end] − pred[loopStart−1] = −7340032 = −7·2²⁰ exactly` — a single quantization step at
that block's scale, i.e. the encoder *tries* to close the loop and misses.

**Decision for now:** `play_wave` keeps the absolute re-stitch (the ideal-DC-removal limit), which
matches the ~0 DC the real engine actually produces. Adding a 20 Hz blocker to the dry path would be
guessing at placement.

## TVF filter TYPE = four SVF taps `[confirmed]`

`FUN_18008ce70` @`0x18008ce70` (the alternate per-voice filter path) dispatches on 2 bits of
`*(param_1+0x24)` to four taps of the same Chamberlin SVF:

| tap | function | output |
|---|---|---|
| LP | `FUN_18008d0a0` | `low` |
| HP | `FUN_18008d2d0` | `in − q·band − low` |
| BP | `FUN_18008d520` | `band` |
| notch | `FUN_18008d740` | `low − high` |

The tap comes from `g_filter_type_coef[block[0x31]]` (exported as `tables/curve_filttype_987b00.bin`)
bits 10-11, copied to `DAT_181a71b60[v]` at voice start. `partial_compute_filter` rejects a type
unless `!(t > 2 && (uint8)(t-4) > 2)`, so:

| `block[0x31]` | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| tap | LP | **HP** | **BP** | *bypass* | **notch** | LP | LP | *bypass* |

Across all melodic partials: 2836 LP, 106 HP, 85 notch, 54 BP, 8 bypass. **Halo Pad P0 is type 1 =
highpass**, P1 is lowpass — we had been lowpassing both. Implemented in `apply_tvf_varying`, and the
old fixed-cutoff `apply_tvf` (with its invented `Q = 0.707·2^((v−64)/18)` from `block[0x4a]`) is
deleted. Q is now neutral 0.707 everywhere, flagged in the docstring, until the real resonance
source (`voice_ctrl_ramp_d` → `DAT_181a1d1f0`) is identified.

## RESONANCE IS `block[0x30]`, and the cutoff law is no longer a fit `[confirmed]`

`FUN_180083f00`'s four dispatch tables (`PTR_LAB_1819a2458/2490/24c8/2500`, stride 0x38 = 7 entries)
were never lifted by Ghidra; they were hand-disassembled from the PE. **Types 0,1,2,4,6 share
identical stages A/B/C and differ only in stage D** (which produces the resonance); type 3/7 bypass;
type 5 has its own cutoff path (not implemented). Cross-check: stage A's arithmetic appears verbatim
in the decompile at `partial_compute_filter` 40080-40086.

```c
/* A  FUN_1800845c0 */ p[0xEE] = clamp(0,0x7f, 2*(0x80 - part[0x456] - part[0x3E7]) + block[0x30]);
/* FUN_180083f00   */ if (p[0xEE] < 4) p[0xEE] = 4;
/* B  FUN_180084200 */ cut = clamp(0,0x7fff, (part[0x3E6]+part[0x455]-0x80)*256 + p[0x1F0] + p[0xEC]);
/* FUN_180084350   */ cut += LFO1_tvf + LFO2_tvf + part cutoff offset   (see below)
/* C  FUN_180084470 */ v = interp(warp[cut>>8], warp[(cut>>8)+1], cut&0xff);
                       cut = min(v, ceil[p[0xEE]]);
/* D  type 0 (LP)  */  q_raw = (p[0xEE]==0x40) ? warp_q_lp[cut>>8]<<2 : p[0xEE]<<11;
/*    types 1,2,4  */  q_raw = p[0xEE] << 11;
/*    type 6       */  q_raw = warp_q_t6[cut>>8] << 2;
/* return          */  p[0xcc] = cut << 2;      p[0xdc] = q_raw;
```

**`block[0x30]` is the RESONANCE**, not the "cutoff key bias" we had labeled it. It is offset by the
part-level GS **TVF Resonance** (SysEx `40 1x 33` → `part+0x3e7`) and its per-program default
(`part+0x456`), both 0x40 when neutral — exactly paralleling the cutoff triple (`part+0x3e6`, SysEx
`40 1x 32` TVF Cutoff, and `part+0x455`) used in stage B. Combined with the earlier ramp finding
`q = (+0xdc>>3)/16384`, `p[0xEE]<<11` at the neutral 0x40 gives **q = 1.0 exactly** — the same
constant the bypass path writes. So **q = resonance_byte / 64**, i.e. `Q = 64/block[0x30]`.

**`block[0x4a]` is definitively refuted as resonance** — it is read once (line 40036), produces
`DAT_181a1f5c0` via `g_reso_curve`, and that is consumed *only* by `tvf_env_level_conv`, scaling TVF
**envelope depth**. No other reader exists in the binary. Likewise `g_filter_type_coef` is never
touched in this chain — it is written once and consumed only by the tap dispatch.

**The empirical cutoff fit is gone.** What `+0xcc = block[0x2f]*633.5 + 176882` was approximating is
`4 × warp(cutoff)` under a resonance-dependent ceiling. The warp table is linear at ~341.33 per 256
cutoff units then saturates, so a two-point linear fit was only locally valid. Decisive
corroboration: `ceil[0x40] = 61440` and `61440*4 = 245760` — *exactly* the "fully open" constant the
earlier calibration measured. That maximum was the neutral-resonance ceiling, not a saturation.

New tables exported (`.rdata` file offset = VA − 0x180000000 − **0x1000**):
`tvf_warp_a83d0.bin` (129×u16, stage C warp), `tvf_ceil_a7ed0.bin` (128×u16, ceiling by resonance),
`tvf_q_lp_a7cd0.bin` and `tvf_q_t6_a7fd0.bin` (256×u16 each, stage D).

`FUN_180084350` (between B and C) sums LFO1+LFO2 TVF depth plus either a part-level cutoff offset or
a mod-source-scaled cents value converted at **≈6.1442 cutoff units per cent** (~737 per semitone) in
the *pre-warp* domain — the hook for CC74 and vibrato-style filter mod when we add the LFO.

## Resonance — the path is traced, the source byte is not (yet) `[confirmed chain]`

The SVF's `q` is a per-voice ramp, and the ramp's seed value is now pinned:

```c
/* voice start, line 81165 */      DAT_181a71c60[v] = *(int*)(partial + 0xdc) << 2;
/* line 77057 */                   uVar25 = DAT_181a71c60[v] >> 2;              /* = partial+0xdc */
/* line 77119, ramp-d source +0x14 */
    *(float*)(&DAT_181a0fb54 + v*0x18) = (float)(int)(short)(uVar25 >> 3) * 6.1035156e-05f;
```
`6.1035156e-05 == 1/16384` exactly, so **`q = (partial+0xdc >> 3) / 16384`**. The filter-bypass path
(`partial_compute_filter` line 39999) sets `partial+0xdc = 0x20000` ⇒ **q = 1.0**, i.e. Q = 1 in the
Chamberlin form (`q = 1/Q`) — that is the engine's neutral. Lower `+0xdc` ⇒ lower `q` ⇒ higher Q.

On the live path `partial+0xdc = DAT_181a22848` (line 40122), which is a field of the 16-byte scratch
struct at `DAT_181a22840` filled by `FUN_180083f00` — the same call that produces the cutoff. So
**cutoff and resonance are computed together, by handlers dispatched on the filter type**
(`param_1+0x1f5` = `block[0x31]`, four function-pointer tables at `PTR_LAB_1819a2500/24c8/2490/2458`).
Which static byte ultimately feeds the resonance is still open — being traced.

This also finally explains the **`+0xc8` vs `+0xcc`** confusion from earlier sessions (lines
40104-40121): the running TVF env level `+0xec` is seeded to 0, `+0xc8 = FUN_180083f00()` is computed
there, then `FUN_180084880` advances the envelope one tick at `g_env_block_speed = 1`, `+0xec` is
reloaded from the new level `+0x40`, and `+0xcc = FUN_180083f00()` is computed again. **`+0xc8` is
the cutoff at env level 0; `+0xcc` is the cutoff after the first envelope tick** — which is why
`+0xcc` is the one that tracks.

Two consequences already applied to `compute_tvf_env`: the envelope's running level **starts at 0**,
not at the release level (all five targets are stored relative to `peak`, then `+0x3c` is zeroed and
`+0xec` seeded from it), and `peak` is folded into the **base**: `voice+0x1f0 = min(0x7fff,
block[0x2f]*0x100 + peak)`.

## Measured against the live engine — and the reference was bad `[confirmed]`

New harness mode `tvftrace` (`tools/decoder`): holds a note and dumps the live per-voice TVF fields
every 100 Hz control tick — `+0xcc` cutoff, `+0xdc` resonance raw, `+0xec` running env level,
`+0x1f0` base, `+0x1f5` type, `+0xee` resonance byte — plus the full 0x220 voice struct per tick and
the audio, so unknown fields can be mined offline.

**Everything we derived statically is confirmed to ~0.1%** (Halo Pad, note 60, vel 100):

| | voice 0 (type 1 HP) | voice 1 (type 0 LP) |
|---|---|---|
| `+0xee` resonance byte | 36 = `block[0x30]` ✓ | 64 ✓ |
| `+0xdc` raw | 73728 = `36<<11` ✓ | 143940 = `q_lp[hi]<<2` ✓ (the type-0 neutral special case) |
| `+0x1f0` base | 16640 = `65*0x100` ✓ | 15872 = `62*0x100` ✓ |
| env peak `+0xec` | 12094 — **exactly** our `segs[0]` | 8062 — **exactly** our `segs[0]` |
| `+0xcc` over 2 s | max error 0.16% | max error 0.09% |

So the resonance law, the cutoff warp/ceiling chain, the env level conversion, the instant segment 0,
and the "env starts at 0" reading are all correct.

### RETRACTION — I was comparing against the wrong instrument

I briefly concluded `real_sweeppad.wav` was an invalid reference. **That was wrong, and the error was
entirely mine.** GM program **94 = Pad 7 (halo)**; **Sweep Pad is Pad 8 = program 95**. Every render
and every `tvftrace`/`holdnote` capture I made this session used program 94, and the resolver printed
`'Halo Pad'` in my own output each time. `real_sweeppad.wav` was a perfectly good capture of program
**95**; regenerating it with `holdnote 95 60 2.4` reproduces the old file's rms and centroid
trajectories **digit for digit**. The "TVF sweeps the wrong direction" problem chased for most of a
session was an artifact of comparing Halo Pad output to a Sweep Pad reference. No "lesson about stale
captures" applies — the capture was fine.

**Sweep Pad, correctly resolved: tone #871, a SINGLE partial** (wave#1807, ping-pong, LP), with
`block[0x2f] = 59` and **`block[0x30] = 23`** — i.e. `Q = 64/23 = 2.78`, genuinely high resonance.
That independently corroborates `block[0x30]` as the resonance byte from the audible side: the
observation months ago that "the real sweep pad has high resonance" while `block[0x4a] = 64`
(neutral) is exactly what you would expect once you know `0x4a` is not resonance. Its TVF rates are
`[68, 50, 52, 69, 71]` — a genuinely slow multi-second sweep, not the instant jump Halo Pad has.

Scores on the *correct* patch, against the (valid, restored) reference:
- **amplitude envelope: r = 0.995**, ours 0.81× the real level
- **spectral centroid: r = 0.545**, ours 1.03× — the level is right on average, the *shape* is not:
  ours opens somewhat faster and wobblier (`345→942` vs `337→795` over the first 2 s)

The live-engine `tvftrace` validation above still stands unchanged: it measured the engine's own
state fields against our computation of them, which is patch-independent arithmetic.

### `tvftrace` on the RIGHT patch — and the 4-tap interpolator

Re-ran the trace on program 95. Our TVF chain matches the engine on Sweep Pad too, and this patch
exercises a path Halo Pad could not (a **non-zero env peak**):

| | real | ours |
|---|---|---|
| filter type | 0 (LP) | 0 ✓ |
| `+0xee` resonance byte | 23 | 23 ✓ |
| `+0xdc` raw | 47104 = `23<<11` | 47104 ✓ |
| `+0x1f0` base | 14284 (= `59*0x100 − 820`, i.e. **peak = −820**) | 14284 ✓ |
| `+0xec` env, 10 ms → 2.4 s | 61 → 14210 (monotonic slow open) | within 0.3% |
| `+0xcc` | — | **max error 0.086%** |

So the `peak` path in `tvf_env_offsets` is now validated too, not just the `peak = 0` case.

**4-tap FIR interpolation implemented** (`g_interp_coef_table` @`0x181a0f210`, exported as
`tables/interp_coef_a0f210.bin`): 128 phase rows × 4 float coefficients, indexed by `phase >> 9`,
every row summing to 1.0. **This region needs a `-0x1400` section adjustment, not `-0x1000`** — the
tell was that the row at the symbol address is the *symmetric* (frac = 0.5) kernel. Even the frac=0
row is `[0.174, 0.653, 0.173, 0]`, a mild lowpass that is *always* applied, where linear
interpolation is `[0, 1, 0, 0]`; at fs/4 the engine kernel passes 0.638 vs linear's 0.707.

Effect on Sweep Pad was small (centroid ratio 1.033 → **1.007**) because that patch plays near
unity ratio, so the interpolator does little work — but it matters wherever notes are transposed:
the piano arp's centroid dropped ~7% across the board (1390→1292 on the first frame), moving toward
the real engine's 919. It is what the engine does, so it stays.

### Scores on both pads (metric fixed)

My first centroid numbers were corrupted by my own metric: it averaged over the silent release tail,
where the centroid of near-silence is meaningless and per-frame ratios explode (Halo Pad scored a
nonsense "ratio 8.27"). Scoring only frames above 2% of peak energy, against fresh `holdnote`
captures of each program:

| patch | envelope r | level ratio | centroid r | centroid ratio |
|---|---|---|---|---|
| Sweep Pad (95) — 1 partial, LP, Q=2.78 | **0.995** | 0.841 | **0.919** | 1.162 |
| Halo Pad (94) — 2 partials, LP + HP | **0.992** | 0.954 | 0.716 | 1.255 |

(The Sweep Pad centroid correlation is 0.92, not the 0.51 I first reported — that was the tail
artifact, not the engine.)

Consistent picture across two very different patches: **amplitude is essentially exact**, the cutoff
trajectory is verified to <0.1% against the engine's own state, and what remains is a systematic
**16–25% excess brightness**. Halo Pad shows it most in shape: the real one starts much brighter
(2416 Hz) and decays faster than ours (1553 Hz, flatter) — that is its *highpass* partial at a
near-maximum cutoff, where the difference between a 2-pole RBJ response and the engine's SVF tap is
largest. That isolates the last item to the filter topology itself.

## THE FILTER IS SOLVED — and the last calibrated constant is gone `[confirmed]`

Read `FUN_18008d9a0` from the **disassembly** (capstone, `.text` file offset = VA − 0x180000000 −
0xC00). Ghidra's C was accurate — the SSE decodes to exactly the naive Chamberlin form, no prescale,
no oversampling, no reordering:

```
xmm7=low  xmm6=band  xmm5=f  xmm4=q  xmm3=in
  xmm0 = f*band ;  low += xmm0 ; store low      (LP tap)
  xmm0 = q*band ;  xmm0 += low
  xmm3 = in - xmm0 ;  xmm3 *= f ;  band += xmm3
```
`FUN_18008d0a0` (LP) and `FUN_18008d2d0` (HP) compute the same thing and differ only in which value
they store — HP saves `high` before the `*f`. So the taps were right.

**What was wrong was `f`.** The ramp *target* is not `+0xcc`; `voice_set_ramp_target_*` passes it
through an exponential lookup first (`DAT_181986420`, 257×int32 = `2^17 · 2^(i/256)` — `T[256]/T[0]`
is exactly 2.0). Working the shifts through:

> **`f = 2^(C/16384 − 15)`**, and since Chamberlin's `f = 2·sin(π·fc/fs)`, **`fc = (fs/π)·asin(f/2)`**.

That is why the engine is stable at what looked like `f ≈ 1.9`: fed the *linear* units the state
matrix diverges, but through the exponential the same patches give `f = 0.05 … 1.23`. My earlier
"the engine must not run the textbook form" conclusion was wrong in its diagnosis — the form was
right, the coefficient was not.

**This also kills the fitted Hz law.** `Fc = 10591·2^((C−245760)/14175)` was **~2× too high** — it
claimed 10591 Hz at `C = 245760` where the truth is 5333 Hz — which was the direct cause of the
long-standing excess brightness. There is now **no calibrated constant anywhere in the TVF path**.
Exported `tables/ramp_exp_986420.bin`.

Results, against fresh per-program captures (frames above 2% peak energy):

| patch | envelope r | level | centroid r | centroid ratio |
|---|---|---|---|---|
| Sweep Pad (95) | 0.995 → **0.998** | 0.84 → **0.980** | 0.925 → **0.9915** | 1.168 → **0.998** |
| Halo Pad (94) | 0.992 → **0.993** | 0.95 → **0.990** | 0.678 → **0.9865** | 1.255 → 1.226 |
| Piano 1 arp | — | — | — | 1.51 → **1.014** |

Sweep Pad is essentially exact (`294,356,308,361,464` vs `337,369,304,361,465`).

**One residual, well isolated:** Halo Pad keeps a flat 1.23× centroid offset even though its *shape*
is now right (r = 0.99). It is the only patch with a **highpass** partial, and that partial carries
just 24% of the mix energy while dominating the centroid; the real mix (2416) sits between our
P1-alone (831) and our mix (3400), i.e. **our P0 is roughly 2× too loud**. Filter, cutoff and Q for
it are all verified against the live trace, so the suspect is the per-partial level. `block[0x50]`
is the obvious candidate but reads **0** for the first partial of both pads (30 and 10 for the
seconds, 80/10 for the piano), so it is not a naive level — it needs tracing before anything is
applied.

### Drums, re-run after the filter fix

Same 34-hit pattern (100 bpm, 8.5 s) through both engines — the harness's `drumsong` mode builds it
in C# and `render_drums` mirrors it in Python:

| | value |
|---|---|
| **centroid** | **r = 0.985, ratio 1.004** |
| envelope | r = 0.904, our level **1.72×** |

**The drum timbre is now essentially exact** — the filter fix carried straight over, with no
drum-specific work. Frame centroids track closely (`5411,5146,6192,6057,6226` vs
`4420,4860,6375,6101,6159`).

What remains is amplitude: both files are peak-normalised, yet our RMS sits ~1.7× the real one
through the body of the pattern (ours plateaus near 0.13, the engine near 0.07). So our *sustain
relative to peak* is too high — either the decays are too slow or the tails too loud.

### Localised with the new `drumnote` harness mode `[confirmed]`

`dll drumnote <kit> <note> <vel> <sec> <out.wav>` strikes ONE drum note on ch10 and renders it
alone. (A lone hit cannot be compared against `drumsong`'s opening — a crash and a hat fire with the
kick there, which is what made my first reading of this wrong.)

Per instrument, the decays are **essentially exact**:

| inst | −20 dB decay, ours vs real | envelope corr |
|---|---|---|
| kick | 100 vs 100 ms | 0.972 |
| snare | 150 vs 150 ms | 0.997 |
| hat | 75 vs 100 ms | 0.991 |
| tom | 400 vs 400 ms | 0.998 |
| crash | 1125 vs 1150 ms | 0.999 |

So "our decays are too slow" was wrong — an artifact of the mixed pattern. **The real defect is
per-instrument LEVEL balance:**

| inst | kit level byte | `block[0x53]` | ours/real peak |
|---|---|---|---|
| kick | 127 | 127 | 1.05 |
| snare | 99 | 127 | **1.77** |
| hat | 105 | 127 | **2.06** |
| tom | 116 | 127 | **0.64** |
| crash | 127 | 127 | **1.67** |

**The kit level byte does not explain it.** Kick and crash both carry level 127 yet need corrections
of 0.95 and 0.60; the tom needs to get *louder* (1.43×) from a mid-range 116. So this is not our
linear `level/127` being the wrong curve — a per-tone factor is missing entirely. All five tones have
`block[0x53] = 127`, so it is not the TVA base level either. The tom is also the only one of the five
with two partials, which we **sum**, so partial combination is a candidate.

### The engine's per-voice amplitude, measured `[confirmed]`

`drumnote` takes an optional 7th arg (a CSV path) and then replays the hit dumping, per control tick,
the voice struct plus **the per-voice amplitude the sampler is actually handed**:

```
amp = *(float*)(DAT_181a1d830 + (v & 3)*0x40 + (v >> 2)*4)
```
written by `voice_ctrl_ramp_a` in `render_block`. Verified to be the TVA gain, not a static level:
the crash's amp holds 1.1255 for ~400 ms then decays exponentially, exactly like its envelope.

**Voice↔partial ordering does NOT always correspond.** Testing both orderings per instrument, the
ratio ours/real becomes *constant within an instrument* under exactly one of them:

| inst | as-is | swapped |
|---|---|---|
| kick | 0.784, 0.527 (spread 0.257) | **0.643, 0.643 (spread 0.000)** |
| snare | **0.629, 0.629 (spread 0.000)** | 1.667, 0.237 |
| tom | 0.448, 0.521 (spread 0.072) | 0.614, 0.381 |

So the kick's two voices are in the opposite order to our partials while the snare's match — any
per-partial comparison must establish the mapping first, or it will compare the wrong pairs. (This
also means the earlier per-partial numbers for Halo Pad should be re-checked the same way.)

### The comparison itself was mono — PAN `[confirmed]`

The kit's `+0x280` plane is **pan**, and it varies per note: kick and snare 64 (centre), hat and
crash 84, tom 34. Our engine renders mono and ignores pan entirely, and the harness was capturing a
single channel — so a pan law was being folded into every level measurement. Captured properly in
stereo, the engine's channel balance is large:

| inst | pan byte | R/L rms |
|---|---|---|
| kick | 64 | 1.0000 |
| snare | 64 | 1.0000 |
| hat | 84 | 1.9214 |
| tom | 34 | 0.3551 |
| crash | 84 | 1.9222 |

### `block[0x0f]` is PER-PARTIAL PAN `[confirmed]`

Prompted by the observation that the "w" (wide) patches ought to expose it: scanning all two-partial
`w`-suffixed tones for a block byte whose partials sit as exact mirror images about 0x40 turns up
two offsets — `0x11` (already known: coarse tune, the *detune* half of a wide patch) and **`0x0f`**.

`0x0f` is pan. 2195 partials sit at exactly 64; **894 are non-neutral**, and the names are decisive:
`St.Soft EP` 24/104, `Cho. E.Piano` 34/94, `Detuned EP 2` 24/104, `Honky-tonk` 59/69,
`Syn.Strings2` 29/104 — every classic stereo/wide patch has an opposite-panned pair.

Verified against the engine with a stereo `holdnote`:

| prog | patch | pans | L rms | R rms |
|---|---|---|---|---|
| 0 | Piano 1 | 64 / 64 | 0.00553 | 0.00553 (equal) |
| 18 | Organ 3 | 44 / 84 | 0.01695 | 0.01308 |
| 50 | Syn.Strings1 | 29 / 99 | 0.02400 | 0.01486 |
| 51 | Syn.Strings2 | 29 / 104 | 0.01303 | 0.01971 |

A centre-panned patch gives identical channel energy; mirrored-pan patches are asymmetric, in both
directions (the imbalance comes from the two partials differing in level and timbre, so mirrored
pans do not cancel). **Our engine is mono and models no pan at all** — so it cannot reproduce the
stereo image of roughly a quarter of all partials. Making the engine stereo is now a real feature
gap, not just a measurement nicety.

### The pan law, recovered exactly — and the engine is now STEREO `[confirmed]`

New harness mode `panscan` sweeps CC10 0..127 on a *sustaining* patch (an organ — a decaying piano
adds ~1% noise) and reports per-channel RMS. The gains come out as **exact multiples of 1/127**, and
centre is **75/127 = 0.5906** — neither the 0.707 of a constant-power law nor the 0.5 of a linear
one, so it is a table, not a formula.

Searching the image for that exact 128-byte run finds it at **VA `0x1819a2fa1`** (exported as
`tables/pan_a2fa1.bin`). It sits *below* where the TVF env-depth code actually indexes
(`≥0x1819a3028`), so there is no conflict with `curve_filtenvdepth_2f00.bin`. The index expressions:

```
left(p)  = T[127 - p] / 127
right(p) = T[p - 1]   / 127        (right of pan 0 is silent)
```
Both index 63 at pan 64, which is why centre is exactly symmetric. This reproduces the measured
sweep with a **maximum error of 0.00037 on both channels** — i.e. exact to the table's quantisation.

`render_note`, `render_drum_note`, `render_drums` and `render_song` now return/write **stereo**;
melodic partials pan by `block[0x0f]` (part-level CC10 applied as an offset from centre, `[likely]`),
drums by the kit's `+0x280` plane. Verified against the engine:

| patch | ours R/L | real R/L |
|---|---|---|
| Piano 1 (centre) | 1.000 | 1.000 |
| Organ 3 | 0.776 | 0.772 |
| Syn.Strings1 | 0.594 | 0.619 |
| Syn.Strings2 | 1.665 | 1.513 |

The centred patch is exact and Organ 3 is within 0.5%. The two Syn.Strings sit 4% and 10% out —
expected, since their image depends on the *relative level* of two oppositely-panned partials, and
per-partial level is precisely the open item above. Pan itself is exact; what is left is level.

Both harness writers now emit stereo (`WriteWavStereo`), and `drumnote`/`holdnote` report per-channel RMS.
Pan 64 gives **exactly** 1.0000, which rules out the naive `(127−p)/p` law (that would give 1.0159);
`L = (128−p)/64, R = p/64` fits to ~1–2% but is still a fit, so **the pan table should be found in
the ROM before anything is implemented**. Our engine needs real stereo output for this to be
comparable at all — that is the next change, and it also matters for melodic parts (CC10).

**The per-voice level error, measured against a left-only capture, was therefore partly this.**
Re-scored against a pan-invariant reference the ratios tightened from a spread of 1.42 to 0.64
(kick 0.74, snare 1.25, hat 0.95, tom 0.61, crash 0.77) — better, but still not constant, so a real
level factor remains on top of the pan artefact.

### The TVA envelope SHAPE is exact; only a per-voice scale is missing `[confirmed]`

Comparing `compute_tva_env` tick-by-tick against the engine's own per-voice gain: for the crash the
ratio is **constant at 0.6665 over the entire 2.4 s note** — the envelope shape, its segment times,
its hold and its decay are all exact, and a single scalar separates us from the engine. Per voice
(engine/ours), each stable over time unless noted:

| inst | kit level | `block[0x53]` | engine/ours |
|---|---|---|---|
| kick v0 / v1 | 127 | 127 / 115 | 1.347 / 1.754 |
| snare v0 / v1 | 99 | 127 / 78 | 1.255 / 1.148 |
| hat | 105 | 127 | 1.444 |
| tom v0 / v1 | 116 | 127 / 117 | 2.057 / 1.729 |
| crash | 127 | 127 | 1.520 |

**Not explained by kit level or `block[0x53]`**: crash and kick v0 share both (127/127) yet differ,
1.520 vs 1.347. So a further per-voice term is missing. (Caveat: the kick and snare rows pair voices
to partials by index, and the kick's ordering is known to be reversed — those two rows may be
mismatched. The single-partial hat and crash are unambiguous.)

This is the cleanest statement of the remaining gap: **envelope timing and contour are solved; only
a per-voice amplitude scale is not.** Two independent measurements are available for it — the engine's
own `DAT_181a1d830` gain, and stereo R/L imbalance on mirrored-pan patches.

**The error is a per-voice SCALAR, not an envelope-shape problem** — the shapes already correlate at
0.97–0.999. Once ordering is matched the constants are: kick 0.643, snare 0.629, hat 0.561,
crash 0.667, tom ~0.48. They cluster near 0.6 but are not equal, and they do **not** fit a monotonic
kit-level curve: kick and crash (both level 127) agree at ~0.655, but solving for an implied
`K(level)` gives `K(116)/K(127) = 1.25` for the tom — greater than 1, i.e. impossible for a level
curve with 127 at maximum. So a per-instrument factor beyond the kit level plane is still missing;
the remaining kit planes (`+0x200` mute/assign group, `+0x300` reverb/flags) and any per-note level
are the next things to check.

### (superseded) Direct SVF port — first attempt, REFUTED `[confirmed negative]`

Both coefficients are known exactly (`f = (int16)(+0xcc>>3)/16384` from the cutoff ramp,
`q = (+0xdc>>3)/16384` from the resonance ramp), so the naive Chamberlin form as decompiled was
ported directly — which would have removed the last calibrated constant. **It diverges.**

Late in Sweep Pad's sweep `f = 1.913` and `q = 0.359`; the state matrix `[[1, f], [-f, 1-qf-f²]]`
has `trace = -2.345`, `det = 0.313`, so `T² = 5.50 > 4·det = 1.25` — **real** eigenvalues, one at
**λ = -2.20**. The filter blows past float range within the note.

Methodological note worth keeping: I first "proved" stability by checking `|λ|² = det = 1 - qf < 1`.
That test only covers the *complex* case, and it passes here while the filter is unstable. The full
condition also needs `|T| < 1 + det` (here `2.345 > 1.313`). The original `f + q < 2` rule of thumb
was right and I talked myself out of it.

**So the engine is not running the textbook Chamberlin form** — `f` reaches ~1.91, far past where
that structure survives. Something keeps it stable: oversampling inside the filter, a prescale on the
coefficient, or an update order that Ghidra's float decompilation has reordered. Resolving that needs
`FUN_18008d9a0` read at the instruction level rather than from the C. Reverted to the RBJ biquad,
fed the correct cutoff and Q; scores are unchanged (Sweep Pad `r = 0.925 / 1.168`, Halo Pad
`r = 0.678 / 1.255`).

The remaining centroid-shape gap is therefore still the filter topology:
we run an RBJ biquad where the engine runs a Chamberlin SVF, fed identical cutoff and Q. Note also
`f = (int16)(+0xcc >> 3)/16384` is the engine's own SVF coefficient — implementing the SVF directly
would remove the `C -> Hz` law (`Fc = 10591*2^((C-245760)/14175)`), the last calibrated constant in
the TVF. Caveat to resolve first: that gives `f` up to ~1.9, beyond the classic Chamberlin stability
limit `f < 2 - q`, so the engine's SVF must differ from the textbook form or be scaled — check
`FUN_18008d9a0`'s coefficient use before porting.

## (superseded) The control RAMP hypothesis — refuted by its own tables

`DAT_1819a7a00 = [4095, 2048, 1024, 682, 512, 409, ...]` is exactly `4096/(2i)`, so with
`step = (target-current)*rate12>>13` at a 1 ms divider the cutoff ramp completes in **4 ms x index —
2 to 40 ms**; the TVA's `DAT_1819a7a30` (`4096/(8i)`) tops out near 160 ms. These are anti-zipper
smoothers and cannot stretch an instant envelope jump into a second-long sweep. Kept because the
mechanism and tables are real and still need implementing for exactness — just not as the
explanation. Side result: `DAT_1819a7a00` is **not** a "start-phase" table as labeled since an
earlier session, it is the ramp RATE word (`partial+0xd0 = 0x4000 + rate`, low 12 bits = rate,
bits 12-13 = divider index).

**(historical) The remaining gap is the control RAMP, which we do not model at all.** With the cutoff chain exact,
Halo Pad P1 now starts at 546 Hz (was 2491) — the right order for the real capture's dark onset — but
the mixed centroid still falls (2687→1553) where the real one rises (337→1346). The reference is
sound: `holdnote` mode zeroes CC91/CC93 (dry), and tone#863 resolves identically in all four maps, so
neither reverb nor a map mismatch explains it. What is missing is that the SVF's `f` is not the
computed cutoff — it is a **slew-limited ramp**. `voice_ctrl_ramp_c` @`0x18005d8d0`:

```c
if (!(flags & 1)) { out = cached_float; return; }        /* not ramping */
counter++;
if ((counter & (int8)DAT_181a03e00[(flags >> 3) & 3]) == 0) {  /* rate divider, 4 entries */
    next = current + step;                                /* step is signed */
    current = (step < 1) ? max(next, target) : min(next, target);
    if (current == target) { step = 0; flags &= ~1; }     /* ramp done */
    out = (float)(int16)(current >> 3) * 6.1035156e-05f;  /* same /16384 normalization as q */
}
```

So the envelope target may jump instantly (Halo Pad's TVF segment 0 has rate byte 0/3 ⇒ zero-length)
while the **filter itself glides** toward it. Ramp structs: `DAT_181a10740` (cutoff `f`) and
`DAT_181a0fb40` (resonance `q`), stride 0x18 — `+0x00` flags, `+0x02` rate word, `+0x04` counter,
`+0x08` current, `+0x0c` target, `+0x10` step, `+0x14` output float.

### The ramp rate law `[confirmed]`

**Update interval** — `DAT_181a03e00 = [0, 7, 31, 127]` (exported as `tables/ramp_divider_a03e00.bin`).
The index is `(flags >> 3) & 3`, and the flag word itself comes from
`DAT_1819a84d8 = [0, 8, 16, 24]` (= idx<<3), selected by **bits 12-13 of the per-voice control word**.
One block is 32 samples at 32 kHz, so the ramp steps every **1, 8, 32 or 128 ms**.

**Step size** — `voice_set_ramp_target_2` @`0x18008b790` (and its `_0`/`_1` siblings), line 78393:

```c
target = exp_lookup(param_4);                       /* piecewise-linear exp, see below */
step   = (target - current) * rate12 >> 13;         /* rate12 = param_5, a 12-bit word */
if (step == 0 && current < target) step = 1;        /* guarantee progress upward */
```

`rate12` is masked `& 0xfff` at the call sites (e.g. line 77224), so it is 0..4095. Because `step` is
computed **once** when a new target is set and then applied linearly, the glide takes
`(8192 / rate12)` updates, i.e. **≈ (8192 / rate12) × (mask+1) ms**. At `rate12 = 4095` that is two
updates; at small `rate12` it is a genuinely slow sweep. So the ramp *can* stretch an instantaneous
envelope jump into a ~second-long glide — the mechanism is real, not just anti-zipper smoothing.

Targets pass through a piecewise-linear exponential first:
`v = (T[u]*(0x40 - (x & 0x3f)) + T[u+1]*(x & 0x3f)) >> 6 >> (0xf - ((x >> 0xe) & 0xf))` with
`u = (x >> 6) & 0xff` and `T = DAT_181986420` — a 6-bit fraction plus a shift exponent.

**Still needed to wire it up:** the source of the per-voice 12-bit `rate12` word and of control-word
bits 12-13 (the arrays `DAT_181a71060/71160/71260/...` read at voice start, lines 77038-77062).

**(superseded) earlier note on the TVF envelope's DIRECTION:** With the taps correct, ours moved
from 3074→1000 Hz centroid to a flattish 2346→1356; the real capture rises **337 → 1346** over 1.8 s.
Ruled out by direct inspection: the sign path in `tvf_env_offsets` is not flipping (`c0` is positive
at every velocity for this patch, so `c2 = 0`), and `peak = 0` because the env-depth key-follow is 0
at key 60. The offsets come out `[+8062, +4471, +6424, +4471]` with `rel = 0` (P1) — i.e. start at
the base cutoff, jump instantly to the brightest (segment 0's rate byte is 0/3 → zero-length), then
close. **Next step:** read `FUN_1800616f0` for which stage register the engine loads *first* and
what the envelope's initial level is — the leading hypothesis is that the stage order or the
start-level assignment is wrong (running the stages 3→0 from `rel` would give dark→bright over
~1.7 s, matching the capture), not that the level conversion is wrong.

## The TVF is a Chamberlin state-variable filter, and resonance is a per-voice ramp `[confirmed]`

Found while looking for the DC blocker — this closes the `block[0x4a]` question from a different
direction. `FUN_18008d9a0` (called by `render_block` per group of 4 voices, SIMD across them) is
the per-voice filter, and its inner loop is:

```c
low  += f * band;                       // *puVar20 = low   -> the voice output
band += (in - (q * band + low)) * f;
```

which is textbook **Chamberlin SVF** (`high = in - low - q*band; band += f*high; low += f*band`),
lowpass tap. Both coefficients are *per-voice ramped control signals*, not static params:
- `f` ← `DAT_181a1cb70`, driven by `voice_ctrl_ramp_c` — the cutoff (matches the existing Fc law).
- `q` ← `DAT_181a1d1f0`, driven by `voice_ctrl_ramp_d` — **this is the resonance**, and in SVF form
  `q = 1/Q`, so *smaller* `q` = more resonant.

So `apply_tvf`'s invented biquad `Q = 0.707·2^((v−64)/18)` should be replaced by an SVF whose `q`
comes from whatever static field feeds `voice_ctrl_ramp_d` — that is the next thing to trace, and
it is a much better lead than guessing among `block[0x30]`/`block[0x31]`. It may also explain the
sweep-rate error, since `f` is a *ramp* (slew-limited), not a per-block jump.

## What I have NOT done / open questions
- **Back-half status**: envelope timing ✓ (100 Hz, `t=0x10000/(rate×speed)×10 ms`), TVF cutoff→Hz ✓
  (2-pole LP, `Fc≈17640·2^((C−245760)/14273)`, ±8%), LFO ✓ (0–20 Hz, sine+6 shapes, 10 cents/unit). The
  DSP domains are now all calibrated. **Next: write the readable reimplementation** (the deliverable —
  static directory + codec + TVA/TVF/pitch/LFO synth) using the Python resolvers + laws as the spec.
- **Minor loose ends**: static `block[0x2f]` cutoff base → `+0xc8` runtime domain (the CC74 sweep moved
  `+0xc8` but not the base), a recheck of `g_reso_curve[8]`, drum tones (4-partial, stride 0x1e8), and
  the effect-algorithm DSP internals (67 `fx_algo_*` located, not dissected).
- Individual effect-algorithm DSP internals (67 `fx_algo_*` located/named, not dissected).
- `fx_algo_orphan66` identity (hidden effect, unreachable).
- Nothing validated against a live debugger; all static + spot-checked vs the engine's own output.

### (earlier open items, now largely closed — kept for history)
- Not analyzed any individual algorithm in `g_fx_algo_dispatch` — reverb topology, chorus,
  delay times are all still opaque.
- Which `voice_ctrl_ramp_*` drives pitch vs amp vs filter is still unconfirmed.
- The voice-state struct layout (the `[16]` interp window + phase accumulator offsets) is only
  partially mapped.
- **Now heavily verified against real audio** (`scvx_engine.py` A/B'd vs the DLL across patches/maps);
  still no live-debugger single-stepping. Remaining engine gaps: 4-tap FIR interp (we use linear),
  TVF cutoff *envelope* (sweep instruments only), LFO in the engine, velocity-layer crossfade, drum
  tones, loop-seam crossfade.

---

## The level chain, SOLVED `[confirmed — exact]`

The per-voice amplitude residual above (1.15–2.06×, "not explained by kit level or `block[0x53]`") had
**two missing terms plus one invented one**. All three are now derived, and the engine's own per-voice
gain is reproduced *exactly from static tables*.

### `tva_compute_base_level` @`180060960` takes three attenuations, not one

The tail of that function is three successive `g_level_curve` subtractions, each clamped to ≥ 1:

```c
iVar4 = uVar3   - g_level_curve[ *(char*)(voice + 0x164) ];   // velocity      -- we had this
iVar4 = iVar4   - g_level_curve[ *(byte*)(voice + 0x140) ];   // ZONE level    -- we passed 127
DAT_181a1f5a8 = iVar4 - g_level_curve[ *(char*)(voice + 0x167) ];  // TONE level -- we passed 127
```

We had been passing 127 (i.e. "no attenuation") for the last two, calling them "part volume" and
"expression". They are neither — both are **static patch data**:

- **`voice+0x167` = the tone header byte `+0x0c`**, a per-tone master level sitting immediately after
  the 12-char name. Literally `voice+0x167 = *(byte*)(voice+0x150 /*tone base*/ + 0xc)` in the
  partial loader. Sweep Pad 105, Halo Pad 115, kick 112, snare 100, tom 127, hat 109, crash 110.
  → `scvx_directory.tone_level(tone#)`.
- **`voice+0x140` = the multisample's per-KEY-ZONE level, plane `+0x6c[zone]`** (0x20 bytes, parallel
  to the key-split bounds at `+0x0c`). It is the out-param of `multisample_select_wave` @`180003420`,
  stored by the partial loader. So a patch can be quieter in one key range than another — Sweep Pad
  is 112 at C4 (zone 1), Halo Pad 114 (zone 5), all five drums 127 (zone 0).
  → `scvx_directory.zone_level(msamp, key, keyCenter)`.

### The sampler is handed `2 × amp_of(level)`

```
engine_amp == 2.0 * amp_of(tva_base_level(...))
```

Verified against the engine's own gain word (`DAT_181a1d830 + (v&3)*0x40 + (v>>2)*4`, written by
`voice_ctrl_ramp_a` every control tick) on **all 8 measured drum voices**, from static tables only:

| inst | voice←partial | engine amp | predicted | err |
|---|---|---|---|---|
| kick | v0←p1, v1←p0 | 0.956909 / 1.166931 | 0.956924 / 1.166949 | 1.8e-05 |
| snare | v0←p0, v1←p1 | 0.930298 / 0.350891 | 0.930312 / 0.350927 | 3.6e-05 |
| tom | v0←p0, v1←p1 | 1.527832 / 1.116638 | 1.527886 / 1.116655 | 5.4e-05 |
| hat | v0←p0 | 1.105286 | 1.105333 | 4.7e-05 |
| crash | v0←p0 | 1.125549 | 1.125597 | 4.8e-05 |

Worst error 5.4e-05 — the harness prints 6 decimals, so this is at the printing quantum. (The kick's
reversed voice↔partial ordering, flagged earlier, is confirmed again here: it is the only one of the
three 2-partial instruments that needs swapping.)

### Level bytes are amplitude-SQUARED; the kit level lives downstream

The kit level plane (`+0x100`) is **not** in the voice gain — the table above matches exactly without
it. It enters in the part-volume computation `FUN_180060390` @`180060390`:

```c
vol = (partLevel * expression * master) >> 6;
if (voice+0x158 != 0)                      // a DRUM part
    vol = (kit_level * vol) >> 7;          // linear
vol = (vol >> 16) * (vol >> 16);           // <-- the part gain is SQUARED
```

so the kit level acts as **`(level/127)²`**, not the linear `level/127` we had invented. Corroborated
independently: `g_level_curve` composed with `g_amp_curve` **is exactly `(l/127)²`** (max deviation
5.7e-05 over the whole range) — level bytes are amplitude-squared everywhere in this engine.

### Partials SUM — the averaging was an invention

`render_note` divided the mix by the partial count. Nothing in the engine does this: `render_block`
@`18008b1d0` dispatches every active voice into one accumulation buffer. The divide was silently
halving every 2-partial patch relative to a 1-partial one. Removed.

### Result: absolute levels now match, with no free constant

Against the real engine's **absolute** RMS (not normalized, not fitted — `drumnote` / `holdnote`
report the raw buffer):

| | real/ours | R/L ours | R/L real |
|---|---|---|---|
| kick | 1.056 | 1.000 | 1.000 |
| snare | 1.011 | 1.000 | 1.000 |
| tom | 1.012 | 0.355 | 0.355 |
| hat | 1.040 | 1.922 | 1.918 |
| crash | 1.009 | 1.922 | 1.922 |
| Sweep Pad | 0.994 | 1.000 | 1.000 |
| Halo Pad | 1.083 | 0.567 | 0.556 |
| Piano 1 | 0.997 | 1.000 | 1.000 |

Drum spread was **1.719 → 1.047**, and the remainder is a near-constant ~1.03 offset (the master/part
constant differs slightly between the drum and melodic paths — `0x1061c` vs `0x10410` in
`FUN_180060390`) plus small filter/interpolation differences. **No calibration constant was fitted**:
every term above is read from a ROM table or a shift in the decompiled arithmetic.

### Loose end found in passing

`program_to_tone(48)` (Strings, SC-8820 map) returns tone# 24590, out of range → renders silent. The
LUT3 entry has its high bits set; that is a directory/indirection bug, unrelated to levels.

---

## The third tone space, and what `voice+0x164` really is `[confirmed]`

Chasing the "program 48 renders silent" loose end turned up two things.

### There are THREE tone spaces, not two

`program_to_tone` read the LUT3 word as a **signed** s16 and treated the sign bit as "unassigned".
That was wrong. The dispatch (`tone_lookup` @`1800011f0`, and the display path @`18006c7ba`) is:

```
v <  0x4000            melodic   g_tone_table_melodic[v]           stride 0x100
0x4000 <= v < 0x6000   drum      g_tone_table_drum[v-0x4000]       stride 0x1e8
0x6000 <= v < 0x8000   INDIRECT  DAT_181a18f20[v-0x6000]           stride 0x18
v >= 0x8000            not directly selectable
```

The third table (50 entries, exported as `tables/layered_1896690.bin`) holds 10 chars of name + `": "`
then two `(map, bank, program)` triples at `+0x10` and `+0x14`, each re-resolved through the *same*
3-level LUT — and the program-change handler `FUN_180068fe0` masks those results with `& 0x7fff`. So
the `0x8000` bit marks a tone that exists but is reachable only *indirectly*.

Four GM programs use it, on the 88Pro and 8820 maps only: **40 Violin, 41 Viola, 42 Cello, 48
Strings**. All 128 programs × 4 maps now resolve; previously these four returned tone# 24590 etc. and
rendered silence.

### The second triple is an alternate ARTICULATION, not a layer

Tempting reading: two tones played together. It is not. Triple 1 is the tone (`part+0x232`); triple 2
goes to `part+0x234` and is named `":L"` in the ROM. `FUN_180003d42` substitutes it for the primary
only when **all** of: `(part+0x12 & 0x20) == 0`, `part+0x3d9 < 0` (mono/solo mode),
`FUN_180068d70(part) >= 2`, and an elapsed-time test `part+0x237 <= now - part+0x238` whose threshold
comes from the entry's `+0x0e`. The ordinary poly path takes `LAB_180003d2d` and it never sounds.

**Measured, not assumed**: `tvftrace` on program 48 shows exactly 2 active voices whose zone levels
are `120` and `127` — precisely the two partials of the *primary* tone 390. Rendering both tones made
us 1.93× too loud; rendering only the primary put Strings at 1.037. `D.alt_tone()` exposes the
alternate for later, when mono mode and the timing test are modelled.

### `voice+0x164` is a per-partial LEVEL, not the MIDI velocity

The engine reported `voice+0x164 = 127` for a note we sent at velocity 100 — so the field we had been
feeding raw velocity into is something else. `partial_velocity_gate` @`180003e90` computes it:

```
b5   = position of vel within the window [block[0x4f], block[0x51]], scaled to 0..127
span = (int8)(block[0x52] - block[0x50])
c4   = (DAT_181a01020[block[0x4e]&0xf][b5] * |span| + 0x7f) * 2 >> 8
voice+0x164 = block[0x50] + (span >= 0 ? c4 : -c4)      (0 -> 1)
```

i.e. the partial's **level crossfaded across its own velocity window** — `block[0x50]` at one end,
`block[0x52]` at the other, with `block[0x4e]`'s low nibble picking the curve shape (row 0 linear,
higher rows progressively concave). The MIDI velocity itself lives at `voice+0x166`.

This finally gives `block[0x50]` and `block[0x52]` their meaning, and it feeds **three** consumers we
had been passing raw velocity: the second attenuation in `tva_compute_base_level`, and both
`env_level_scale` rate multipliers (`@18005f0aa` reads `+0x164` for all three). Curves exported as
`tables/curve_velxfade_01020.bin` / `curve_velsens_01520.bin`.

Verified exact on 7 instruments / 11 partials against the live voice struct (the one apparent
mismatch, Strings, is the known voice↔partial ordering — as a *set* it matches).

### Result

Absolute RMS vs the real engine, after both fixes:

| | before | after | | before | after |
|---|---|---|---|---|---|
| kick | 1.056 | 1.056 | Violin | — silent — | 1.102 |
| snare | 1.011 | 1.011 | Viola | — silent — | 1.073 |
| tom | 1.012 | 0.999 | Cello | — silent — | 1.048 |
| hat | 1.040 | 1.040 | Strings | — silent — | 0.997 |
| crash | 1.009 | 1.009 | Sweep Pad | 0.994 | 0.994 |
| | | | Halo Pad | 1.083 | 0.990 |
| | | | Piano 1 | 0.997 | 0.997 |

Overall spread **0.990–1.102** across 12 instruments. The exact 8-voice per-voice-gain test still
passes at 5.4e-05.

---

## The LFO, solved and routed to pitch `[confirmed — bit-exact]`

### Two LFO engines, not one

Each note runs **two** LFO objects and a partial is modulated by both:

| | LFO1 `lfo_update` @`180081b90` | LFO2 `FUN_1800823b0` @`1800823b0` |
|---|---|---|
| selected by | LFO-object `+0x02` == 1 | == 2 |
| params live in | the **tone header**, `0x0e..0x15` | the **`0x6e` partial block**, `0x06..0x0d` |
| depths (pitch/TVF/TVA) | block `+0x15` (s8) / `+0x34` (s16/2) / `+0x56` | `+0x16` / `+0x36` (s16/2) / `+0x58` |
| rate field | an **index** into `g_lfo_rate_tbl` | a **raw** per-tick phase increment |
| delay field | an **index** into `g_lfo_delay_tbl` | a **raw** per-tick increment |
| pitch depth | through `g_lfo_cents_tbl` | already in final units |

That index-vs-raw asymmetry is real, not a mis-read: measured live, LFO1's rate 43 produced
increment **2817** (= `g_lfo_rate_tbl[43]`) while LFO2's rate 917 produced increment **917**.

Objects live in a pool of 128 x `0xa8` reached through the accessor at `module+0x5c340`. The
liveness test is the **type byte at `+0x02`**, not `+0x00`. A delay field yielding rate 0 (the
stored `-1`) means the delay accumulator never saturates, so that LFO is silent forever — that is
how Piano and Sweep Pad switch LFO2 off. Waveform byte: bits 0-4 index (through `g_lfo_wavemap`),
bit 5 mod-source, bits 6-7 initial phase.

### The unit is MILLI-SEMITONES, not cents

`g_lfo_cents_tbl` is not cents. The pitch accumulator `FUN_1800830e0` clamps to `0x1f018` =
**127000** = 127 semitones x 1000, which fixes the unit at 1/1000 semitone. So the table's maximum
of 6000 is **6 semitones**, not the 6000 cents an earlier note claimed, and a depth byte of 6 gives
60 units = **6 cents**. Ratio multiplier = `2^(units / 12000)`.

This was the whole of an apparent 8x discrepancy between the engine's `mod_pitch` (+/-60) and the
+/-7.5 cents I measured in its audio.

### Validation

`scdec lfotrace <prog> <note> <sec> <csv>` dumps every live LFO object per control tick (phase,
rate, delay/fade accumulators, depths, and the three mod outputs). Against MutedTrumpet (prog 59):

| | engine | ours |
|---|---|---|
| LFO1 increment | 2817 (4.298 Hz) | 2817 |
| LFO2 increment | 917 (1.399 Hz) | 917 |
| LFO1 delay/fade rate | 2100 / 753 | 2100 / 753 |
| LFO2 delay/fade rate | 6553 / 3276 | 6553 / 3276 |
| `mod_pitch` per tick | — | **199/199 ticks identical, max diff 0** |

End-to-end, measuring the rendered audio with a pitch tracker (itself calibrated on synthetic
vibrato: +/-60 cents in -> +/-58.8 measured):

| patch | ours | real engine |
|---|---|---|
| MutedTrumpet | 7.2 cents @ 4.38 Hz | 7.3 cents @ 4.38 Hz |
| Flute | 3.6 cents | 4.4 cents |
| Piano | 1.5 (flat) | 1.4 (flat) |

All level measurements are unchanged by this, which is the expected result — vibrato does not move
RMS. Had they shifted, that would have signalled a wiring error rather than progress.

### Two corrections to things I said earlier in this session

1. **"LFO1's pitch depth contributes nothing by default" was wrong.** I inferred it from Flute
   measuring flat. Flute's LFO1 pitch depth is 3 = **0.3 cents**, which is simply below the
   tracker's noise floor. The live trace shows LFO1's depth passing straight through
   (`g_lfo_cents_tbl[6]` = 60 for MutedTrumpet). The measurement was right; the inference from it
   was not.
2. **"MutedTrumpet's rate is 1.40 Hz but measures 4.33"** was a false discrepancy — I was comparing
   the audio against LFO2's rate while the audible vibrato is dominated by LFO1 at 4.298 Hz. Both
   oscillators were correct all along; only my attribution was wrong. Reading the engine's own
   phase counter, rather than inferring rate from audio, is what settled it.

### Not modelled

LFO shapes 1/2/3 are the random ones (S&H plus two slew-limited variants) driven by `prng_lfsr`, a
Galois LFSR. They need the engine's RNG state, so `lfo_wave` returns 0 for them rather than
substituting invented noise. Several patches use them at low depth for subtle instability (Tuba,
Oboe, Acoustic Bass). The TVF and TVA LFO depths are decoded but not yet applied.

### All three LFO destinations routed `[confirmed — 1194/1194 ticks bit-exact]`

The remaining two destinations are now applied as well:

- **TVF** — `FUN_180084350` **adds** the mod to the runtime cutoff and clamps to `[0, 0x7fff]`,
  i.e. the same 15-bit domain `compute_tvf_env` already produces. Depth `block+0x34` (LFO1) /
  `+0x36` (LFO2), each **divided by 2**.
- **TVA** — `FUN_180060390` folds it into the voice volume as a fraction of `0x7f00`:
  `vol' = vol + vol*mod/0x7f00`, with the summed mod clamped to `+/-0x7f00`. Depth `block+0x56`
  (LFO1) / `+0x58` (LFO2), used directly.

Rounding differs by destination and it matters: TVA goes through `FUN_1800828f0` (**ceiling**,
`+0xffff`), TVF and pitch through `FUN_180082940` (**round to nearest**, `+0x8000`). Otherwise both
are the same sign-magnitude fixed-point multiply.

One real bug caught by the bit-exact test: the TVF depth is `(s16)/2` in C, which **truncates
toward zero**, where Python's `//` floors. A stored `-179` must give `-89`, not `-90`. That was a
1-unit error on 75 of 199 ticks — invisible in audio, but it is exactly the kind of drift that
accumulates into "close enough" reimplementations.

Final agreement, all six series (2 LFOs x 3 destinations) against the live objects:

| | pitch | TVF | TVA |
|---|---|---|---|
| LFO1 | 199/199 | 199/199 | 199/199 |
| LFO2 | 199/199 | 199/199 | 199/199 |

**An independent cross-check fell out of this.** Routing the TVA LFO was done purely for
correctness — but it also closed the largest remaining *level* gap, which I had not been targeting:

| | before | after |
|---|---|---|
| Violin | 1.102 | **0.998** |
| Viola | 1.073 | **1.002** |
| Cello | 1.048 | **0.997** |

Those three patches carry negative TVA LFO depths, which pull the mean level down; without the
tremolo we were rendering them too loud. Overall spread across 12 instruments is now
**0.990-1.056** (was 0.990-1.102). A model derived for one reason predicting an unrelated
measurement is the strongest evidence available that it is actually right.

Pitch is unchanged and still matches (MutedTrumpet 7.2 cents @ 4.38 Hz vs the engine's 7.3 @ 4.38).

### Jetplane (prog 125 bank 7) — two bugs it exposed `[confirmed]`

Trying an SFX patch found two real defects that the melodic/drum test set never touched.

**1. `render_note` had no bank parameter.** It always resolved CC0 = 0, so every "variation"
patch silently rendered the bank-0 tone. Asking for Jetplane rendered *Helicopter* — and the
resolver printed the name in plain sight. Fixed: `render_note(..., bank=)`, and the harness's
`holdnote` / `lfotrace` now take a bank argument too.

**2. An LFO2 delay field <= 0 means INSTANT, not disabled.** I had modelled `delay <= 0` as
"the delay accumulator never saturates, so this LFO is silent forever". Measured, a stored `-1`
yields `delay_rate = 65535` on both Jetplane and Piano — the LFO is active *immediately*. The
correct rule for LFO2 is `delay_rate = field > 0 ? field : 0xffff`; LFO1 keeps its table lookup
(`g_lfo_delay_tbl[field]`, and `[0] = 65535` gives the same "instant" answer).

This silently disabled LFO2 on every patch with a non-positive delay field — Flute among them.
It survived the earlier bit-exact test purely because MutedTrumpet's field is 6553 > 0. **A
1194/1194 tick match proved the code path I tested, not the ones I did not.** Re-validated after
the fix: still 1194/1194.

**Where Jetplane stands.** Spectral balance is close (energy below 200 Hz: ours 1.000, real 0.989)
and the amplitude envelope correlates at 0.959, but the absolute level is **real/ours = 1.32** —
much worse than the 0.99-1.06 we hold elsewhere. The patch's two partials both drive LFO2 with
**waveform 1, the `prng_lfsr` random S&H**, at large depths (TVA -3251 / -975, TVF -128 / -64), and
we render that as zero. The real capture has energy at 200-800 Hz and above 8 kHz that ours lacks
entirely — the filter being flung open by random TVF modulation.

**The random LFO is reproducible in principle.** `prng_lfsr` @`18008fbb0` is two 16-bit registers:
A is a right-shift LFSR with `newbit15 = bit15 XOR bit5`; B left-shifts in `bit2 ? !bit13 : bit9`;
the return is `B ^ A`. The seeds after reset are deterministic (`A=0xEFA6, B=0x9C23`, identical
across runs and across programs). A first-cut simulation — objects walked in ascending index, one
draw per object whose phase wrapped — reproduces **316/398** ticks (79%), so the arithmetic is
essentially right but the draw accounting is not yet exact.

Two caveats before anyone finishes it: the LFSR is a **single global shared by every voice**, so
the sequence depends on how many oscillators consume it and in what order; and the object indices
come from a freelist. Both are stable for an isolated note (which is how we render) but cannot be
reproduced inside a polyphonic mix without modelling the whole pool's history.

### Broadening the pitch-envelope test set — three more corrections `[confirmed]`

The first validation covered **one partial of one patch**. Given what the LFO delay bug had just
taught me (a 1194/1194 bit-exact match proved only the path I tested), I widened it to 11 voices
across 6 patches before building anything further. That immediately found three defects.

**1. Zero-time segments are not skipped — they take exactly one tick.** The rate word is
`T < 11 ? 0xffff : 0xa0000/T`, and `0xffff` wraps the 16-bit phase in one control tick. Mellow Gt.,
whose four segments all have rate byte 0 or 1, steps `0 -> +76 -> +153 -> 0` on consecutive ticks;
my code jumped straight to the last target and lost the +153 excursion entirely.

**2. The envelope is STEPWISE at control rate, not a continuous ramp**, and tick *i* is the state
*after* that tick's update. Modelling it as a continuous per-sample ramp is invisible on long
segments (sub-milli-semitone) but dominates on 10 ms ones.

**3. Segment completion is `phase >= 0xffff`, not `> 0xffff`, and the next segment starts fresh
with no phase carry.** Both follow directly from measurement: Nylon-str.Gt's tick 0 reads **exactly
+43**, its segment-0 target, and the following 88 ms segment then ramps over 9 ticks starting from
there. I briefly implemented a phase carry — it made the all-fast-segment case exact and every
mixed-timing case worse, which is how I knew it was wrong.

After all three, running the engine's own segment machine tick by tick:

| patch | voices | max error |
|---|---|---|
| Nylon-str.Gt | 1 | 0.9 mst |
| Cimbalom | 2 | 1.0 mst |
| Open Hard 1 | 2 | 0.4 mst |
| Overdrive 2 | 2 | 0.4 mst |
| Jetplane | 2 | 0.9 mst |
| Mellow Gt. | 2 | 0.0 / **5118** |

**10 of 11 voices within 1.0 milli-semitone (0.1 cent).**

### The one outlier, unexplained

Mellow Gt.'s partial 0 does not fit. Its start offset reads +304 where we predict +4995, and its
first segment settles in 9 ticks where we predict 1479 ms — both roughly 16.4x, which is suggestive
but not an explanation, so no constant has been applied. A velocity sweep shows the excursion
scaling faster than our linear depth-velocity law (engine +81/+304/+429 at velocity 40/100/127,
ours +1998/+4995/+6342), and its *resting* pitch also moves with velocity (61893/64647/65861),
which means a velocity-switched wave with a different root is in play.

Scope of the risk: of 195 partials with a pitch envelope, **13** combine a large depth (>1000) with
non-zero depth-velocity-sensitivity — the combination that fails here. Patches with small depths and
the same velocity sensitivity (all the guitars, depth 70-100) validate to 0.4-0.9 mst, so the
velocity law is not simply wrong; something specific to this configuration is missing.

No regression: the 12-instrument absolute-level spread stays 0.990-1.056.

### The "16.4x outlier" was a bad comparison, not a model error `[retracted]`

I previously recorded Mellow Gt.'s partial 0 as an unexplained ~16.4x discrepancy in both start
level and first-segment time, and speculated about a velocity-switched wave. **Both claims were
wrong.** Widening the trace set to 20 voices across 11 patches settled it:

- The waves do **not** change with velocity on that patch — same wave 685 at velocity 40/100/127.
- **Ice Rain (prog 96) partial 0 has byte-identical pitch-envelope parameters** to Mellow Gt.
  partial 0 — biases `[57, 0, -20, -30, 0]`, rates `[57, 64, 73, 48, 73]`, differing only in depth
  (8310 vs 7000) — and it validates at **0.9 milli-semitones over 199 ticks**.
- Mellow Gt.'s voice 0 has its **cutoff and envelope level frozen from tick 0** and its pitch stops
  after 9 ticks. Ice Rain's equivalent voice runs the full 200. That is a voice ending, not a
  different envelope law.

The measured slopes matched all along (-34/tick observed vs -33.75 predicted); only the *duration*
differed, because the engine stopped stepping a voice we kept simulating.

Final validation, predicting each voice's pitch from the static block alone:

| | |
|---|---|
| voices within 2 cents | **18 / 20** |
| worst of those | 1.4 milli-semitones (0.14 cent) |
| outliers | Mellow Gt. v0, Bird 2 v0 — both cases where the engine **stops stepping** earlier than we do |

Bird 2's segment slope also matches exactly (-2104/tick predicted and observed); it too simply halts,
after segment 1, while our model continues through segments 2 and 3.

**So the open question is not the envelope law but its termination condition** — what makes the
engine stop advancing a pitch envelope. That is plausibly the same missing piece the decompile could
not give us: the stage-advance handlers reached through `PTR_1819a17c8`, whose targets are absent
from the file. Worth noting the practical impact is small — it only matters once a voice has
effectively stopped contributing.

*[Later resolved in part: the handler targets were recovered from the DLL image and the full stage
machine is documented in "TVP — the pitch-envelope runtime machine, recovered" below. The two
outliers here are still not explained by it — their halt is in the voice-pair kill path, not the
envelope.]*

**Method note, earned twice now:** a validation set of one is worth very little. The single-patch
check reported 1.2 cents and hid three separate defects; the 6-patch check found them but produced a
*false* anomaly that only an 11-patch set could refute. Both times the fix was more data, not more
theory — and on both occasions the tempting move was to introduce a constant (16.4x here, 1.5x for
levels, 2x for cutoff earlier) that would have buried the real explanation.

## Jetplane solved — `render_note` was ignoring the partial key transpose `[confirmed]`

The remaining Jetplane error was not the random LFO, not DC, and not the envelope law. It was that
**`render_note` never applied `block[0x10]` (whole-semitone partial transpose) or `block[0x11]`
(coarse tune)** — only the drum path did. Jetplane's partial 0 carries a **-36 semitone** transpose
and its partial 1 **+24**.

That mattered far beyond tuning, because of a second omission: I had deliberately worked in
*offsets from base* so the base chain would cancel. But the engine **clamps the absolute pitch to
[0, 0x1f018]**, and Jetplane's partial 0 sits exactly on that floor — base 24000 with a -24000
envelope start gives precisely 0. An offset-relative model cannot express that clamp, so we played
the partial at ratio 0.25 where the engine plays it at **0.031** (five octaves down, a near-frozen
crawl). That single partial, played 8x too fast with a DC-heavy sample, was the "DC problem".

### The base pitch chain

```
key  = note + (block[0x10] - 0x40)                whole-semitone partial transpose
row  = (block[0x13] - 0x40) >> 2                  row 0 is flat (no key follow)
base = key*1000 + g_kf_pitch[row][key] + (block[0x11] - 0x40)*10        milli-semitones
pitch = clamp(base + pitch_env + lfo, 0, 0x1f018)
ratio = 2^((pitch - (root*1000 + 1024 - fine)) / 12000)
```

`g_kf_pitch` (`tables/kf_pitch_01b20.bin`, 8 rows x 0x80 s16) explains the constant -11 that had
been showing up in every base: that is row 2 at key 60, the row Piano uses. Jetplane's partials use
row 0 and show diff 0.

### Validation — absolute, no anchoring

Previously I compared shapes by anchoring predictions to the engine's tick-0 value. With the base
chain implemented, the comparison can be absolute:

**22 / 22 traced voices predict the engine's own `voice+0x6c` to within 11 milli-semitones**
(most exactly 0 or +/-1), across 13 patches spanning piano, guitars, organs, pads, leads and SFX.

### Jetplane, finally

| % of energy | <30 Hz | 30-100 | 100-500 | 2k+ |
|---|---|---|---|---|
| real engine | 65.05 | 6.83 | 28.08 | 0.05 |
| **ours** | **64.78** | **6.81** | **28.31** | **0.10** |
| ours, before | 99.51 | 0.01 | 0.47 | 0.00 |

Absolute level **real/ours = 0.987** (was 1.302). The real engine's sub-30 Hz dominance is genuine
signal after all — a sample played at 1/32 speed — exactly as the pitch clamp implies.

### Scope: this was silently detuning ~15% of the library

**147 of 985 sounding partials carry a non-neutral `block[0x10]`**, across 126 program/bank pairs,
and 295 carry a non-neutral coarse tune. They are precisely the patches built on octave doubling —
Church Bell -24, Harpsichord3 -12, Coupled Hps. +12, E.Organ 16+2 at +19/-12. Those all rendered
without their octave layers until now. The 12-instrument regression set never caught it because
every one of those patches has a neutral transpose — another instance of a narrow test set hiding a
broad defect.

### The aliasing: the zone lookup must use the TRANSPOSED key `[confirmed]`

Jetplane's audible "mirroring" was genuine aliasing, and it came from the wave *selection*, not the
pitch. `multisample_select_wave` @`180003420` is handed the voice's key -- which
`partial_compute_pitch` has **already shifted by `block[0x10]`** -- and only then adds
`(0x40 - keyCenter)` itself. Our resolver used the untransposed note.

For Jetplane's partial 1 (+24 semitones) that picked **wave 215 (root 60)** from the bottom zone,
which the pitch chain then had to stretch to **4x** -- reading four source samples per output
sample through a 4-tap interpolator, i.e. textbook aliasing. With the transposed key the
multisample selects **wave 217 (root 84)** from its 6-zone map and plays it at **0.79x**.

Confirmed against the engine: `voice+0x1fc` (the sample's own root pitch) reads **84277** for that
voice, which is wave 217 exactly (`84*1000 + 1024 - 747`), and 60000 for partial 0 = wave 3124.

### Short-loop interpolation wrap `[confirmed]`

Second, smaller cause. The 4-tap window reaches `i0+2`, so near the loop end it must read the
samples the loop *wraps to*. Appending a single sample left `_interp4` clamping its index there,
glitching once per loop pass. Harmless on a long loop -- but wave 217's loop is **29 samples**
(~1.1 kHz), so the glitch landed as a broadband buzz directly under this patch's resonant 7.8 kHz
highpass. Fixed by extending the buffer with the wrapped samples (repeating the loop when it is
shorter than the tap reach).

Jetplane 2-8 kHz energy: **0.054% -> 0.043%** against the real engine's 0.008%; 8-16 kHz
0.083% -> 0.065% against 0.038%. Better, not yet equal.

### What is NOT the cause, checked and eliminated

- **The filter.** Our cutoff units (253428), q (0.0625) and f (1.381 vs 1.383) match the engine's
  live `+0xcc`/`+0xdc` exactly, as do the resonance byte (4) and filter type. It is a genuine
  resonant highpass at 7.8 kHz with Q~16 in both.
- **Filter instability.** |eigenvalues| = 0.956; the impulse response decays by 10 orders of
  magnitude within 1000 samples.
- **Partial 1's level.** Its amp matches the engine to 5 decimal places (0.040192 vs 0.040161),
  and it contributes 100% of the 2-8 kHz energy.

### New open item: partial 0's amp is 2.05x the engine's

Adding the per-voice amp to `tvftrace` turned this up. For Jetplane, `2 * amp_of(tva_base_level)`
gives **1.1258** where the engine's own gain word reads **0.4467-0.5490**. Partial 1 matches
exactly, so this is not the global `TVA_AMP_SCALE`.

Note our *total* RMS still matches at 0.987, which means a second error is compensating -- our
partial 0 signal must be correspondingly quiet before the gain. Two errors cancelling in the
aggregate is exactly the situation aggregate metrics cannot reveal, and it was only visible because
the per-partial HF pointed at it. Worth chasing next.

#### Retraction: partial 0's amp is NOT 2.05x off

Corrected within the hour. I compared our envelope's segment **target** (1.1258) against the
engine's **in-progress** gain (0.4467-0.5490) -- but the envelope never reaches that target during
the note. Comparing the full trajectory tick by tick:

| | engine | ours | median ratio |
|---|---|---|---|
| partial 0 | 0.44672 .. 0.54895 | 0.00000 .. 0.54943 | **1.000** |
| partial 1 | 0.00037 .. 0.04016 | 0.00000 .. 0.04019 | **0.991** |

The TVA is exact here too, and there is **no compensating-error situation** -- that inference was
built on the bad comparison. The `min 0.000` is our envelope starting at zero where the engine's
first sampled tick is already mid-attack: the same one-tick alignment seen throughout.

Lesson repeated: comparing a *steady-state parameter* against a *time-varying measurement* is not a
comparison. The earlier "16.4x" outlier had exactly the same shape -- an anchoring error dressed up
as a discovery. Both times the tell was a suspiciously round factor.

## Level sweep: 72 voices, velocity 20-127, keys 36-84 `[confirmed]`

Levels had been validated exactly on 8 drum voices plus 12 aggregate RMS figures; pitch had 22
voices. Given that a narrow set has hidden a broad defect three times this session, the level chain
got the same treatment: 40 traces over 10 programs x {key 36 vel 40, key 60 vel 100, key 84 vel 127,
key 60 vel 20}, chosen to stress velocity crossfade (`block[0x50] != block[0x52]`), level
key-follow, and multi-zone multisamples (up to 18 zones). Metric: max |ours - engine| across the
note as a fraction of the engine's peak gain, from its own per-voice amp word.

**62 / 72 voices within 5%; median error 1.42%.**

### Defect found: the envelope never reached silence

`g_amp_curve_hi[0]` is **4, not 0**, so `amp_of(0)` returns 4.6e-05. Our stage gain used
`max(0, base16 - g_level_curve[stage])`, which clamps a *negative* level to zero and therefore maps
it to the amplitude table's floor rather than to silence. Every partial that decayed to nothing sat
at -80 dB forever. The engine's own gain word reads **exactly 0.000000** in that state (measured on
Piano 2's second partial). Fixed: a stage whose attenuation exceeds the base is silence.

Inaudible in isolation, but it is wrong, and with many finished voices in a mix it accumulates.

### Tested and rejected: a stepwise control-rate gain

The engine's gain word is a control-rate value, so modelling the TVA as stepwise per 320-sample
block *looks* more faithful than our smooth per-sample ramp. It measures **worse**: 59/72 within 5%
and median error 2.74%, against 62/72 and 1.42% for the smooth version. That is consistent with the
env block's **anti-zipper ramp word at +0x02** -- the engine interpolates the gain across the block
rather than stepping it. Recorded because the plausible-looking change was the wrong one, and only
the sweep could tell.

### Remaining outliers

All 10 cluster on two patches: **Strings (prog 48)** at 6-30% and **Piano 2's second partial** at
42-52%. Both are quiet, fast-decaying layers (peaks 0.06-0.35 against a main partial near 1.0)
whose envelopes collapse within one or two control ticks. The error is in the decay *shape* over
those few ticks, not the level -- the peaks agree. Likely the same one-tick alignment seen
throughout, amplified because the whole envelope is a few ticks long, plus the invented 3 ms attack
floor in `compute_tva_env`. Worth a pass, but the audible stake is small.

### The attack floor: investigated, still empirical `[open]`

`compute_tva_env` floors the attack segment at 3 ms "to avoid a click" -- an invented constant, the
kind the project tries to eliminate. I tried to derive it and could not, cleanly.

The segment machine says a zero-rate segment takes exactly one control tick = **10 ms** (rate word
saturates at 0xffff, wrapping the phase in one tick -- the same rule the pitch envelope uses), and
the engine's rate-0 piano attack does measure 50% at 10 ms. But flooring to 10 ms measures **worse**
on every available yardstick:

- level sweep: 62 -> 61 voices within 5%, median error 1.42% -> 2.99%
- drum RMS: spread 1.056 -> 1.074 (a 10 ms attack is simply too long for percussion)

The control-rate amp word cannot resolve sub-tick attack behaviour, and the audio onset is entangled
with the sample's own transient, so I have no measurement that isolates the true attack shape. Rather
than swap one un-derived constant for a worse-fitting one, the 3 ms is kept and flagged. The likely
resolution involves the env block's anti-zipper ramp word (`+0x02`) governing how the first block is
smoothed, which needs a sample-accurate trace the current harness does not provide.

## Live CC7 (volume) + CC11 (expression) `[confirmed — exact]`

The level chain reproduced the STATIC patch levels but rendered at a fixed CC7 = 127, so any file
automating volume or expression -- string swells, fades, mix rides, nearly every real MIDI file --
was wrong in its dynamics. Now wired.

`FUN_180060390` @180060390 computes the part-level volume (line 39318):
```
u   = ((CC11 * CC7) & 0xffff) * master >> 6 & 0xffff        (CC7 volume, CC11 expression)
u2  = (u * 0x10410) >> 16
vol = u2 * u2                                               (squared)
```
It is applied DOWNSTREAM of the per-voice amp word (which we already match at 127), so we apply the
factor RELATIVE to the 127/127/127 reference: `part_volume_scale(cc7, cc11, master)`. CC7 and CC11
enter symmetrically as their product -- confirmed, equal values give equal output.

Verified against the engine's own audio RMS, matched hold/tail, mono piano (combined = per-channel
x sqrt2):

| CC7 | CC11 | real/ours |
|---|---|---|
| 127 | 127 | 0.997 |
| 64 | 127 | 1.000 |
| 127 | 40 | 0.990 |

and the relative response is exact to 3 decimals across a 16x range (CC7 100/64/32 ->
0.620/0.254/0.0635 predicted vs 0.6203/0.2532/0.0633 measured). `render_note` and `render_drum_note`
take `cc7`/`cc11`/`master`, defaulting to 127 (no change on the existing regression: drum per-voice
gain still 5.4e-05, 12-instrument spread still 0.990-1.056).

**Scope note:** this is a per-note STATIC value. A real file rides CC7/CC11 *within* a note (an
expression swell), which needs the sequencer layer to track per-part CC state over time and feed a
time-varying scale -- a follow-up when MIDI-file playback is built. The law itself is exact; only the
plumbing to vary it over a note is outstanding.

## Pitch bend + part tune `[confirmed — exact to phase-increment quantization]`

Two pitch modifiers, both additive into the absolute pitch we already compute (@72896/72911):

**Pitch bend** (`part+0x448`, applied `>>13` at line 72896). The RPN bend-range storage was flagged
NOT ESTABLISHED, so rather than trace the message plumbing I measured the engine's own phase
increment `voice+0xbc` across a bend sweep at several RPN ranges. The net law is clean and linear:

  **offset_semitones = (bend14 - 8192) / 8192 * range**,  range from RPN 00/00 (GM default 2).

Confirmed at range 2 (+/-2 st at the wheel extremes, +/-1 at half) and range 12 (+/-12, +/-6). Our
implementation matches the engine's applied pitch to **0.008 semitones** across the sweep -- the
residual is the increment quantization (`voice+0xbc = ... & ~1` drops the low bit; the `*512/375`
scaling loses precision), not a law error.

**Part tune** (`part+0x3ba`, added directly at line 72911) is a plain s16 milli-semitone offset --
the net of RPN coarse tune (00/02), RPN fine tune (00/01), and GS part key-shift/tune. Exposed as
`tune_ms`, a direct add.

`render_note` takes `bend` / `bend_range` / `tune_ms`, defaulting to 8192 / 2 / 0 (bit-identical to
before). These fold into the same clamped pitch as the envelope and LFO, so bend correctly saturates
at the 0 / 0x1f018 bounds. New harness path: `tvftrace` takes bend + RPN-range args and dumps
`voice+0xbc`.

**Scope, same as CC7/CC11:** this is a per-note static value. A real file rides the pitch wheel
*within* a note (that is the whole point of bend), which needs the sequencer layer to feed a
time-varying offset. The law is exact; the time-varying plumbing is the follow-up.

## Per-program filter defaults — NOT a gap (agent false positive) `[confirmed]`

The MIDI-layer audit flagged `part+0x455/0x456/0x457` (per-program TVF cutoff / resonance / env
defaults, loaded from a preset table @56112 and summed into the filter @40080/40209) as "non-neutral
per patch -- zeroing them shifts the filter for many instruments." That inference was from the
existence of the preset-loading code, not from its data.

Measured directly: read `part+0x453..0x45b` via the active voice's part pointer (`voice+0x128`) after
a program change, for **all 128 programs x banks 0-8**. Every field is **0x40 (neutral) in every
case**. The preset loader (`FUN_180068fe0` @56095) has a neutral branch (`cVar2 == 1` ->
`0x40404040` @56100) and a drum branch (@56126); on the default map the loaded values are neutral
throughout.

Our filter code already carries the hooks (`tvf_reso_byte(raw, part_reso, part_reso_default)` with
both defaulting to 0x40 -- exactly the engine's `((0x80 - part[0x456]) - part[0x3e7])*2 + block[0x30]`
@40080), so passing 0x40 is not an omission, it is correct. **No code change; the gap does not exist
on the map real files use.**

Caveat, untested: the loader's preset path is gated by `part+0x44d/+0x44e` (a map/mode selector).
The SC-55 / SC-88 legacy maps might take that path and load non-neutral presets. Reaching them needs
a map-select SysEx the harness does not send; if a file explicitly selects a legacy map, this could
become a narrow real gap. For the native map (the default, and what almost all GM/GS playback uses),
it is settled: neutral. Harness: `tvftrace` now prints `part+0x453..0x45b` for the active voice.

## Non-default reverb + chorus TYPES (GS macros) `[confirmed — reverb exact; chorus level-corrected]`

The transcribed reverb (`fx_reverb_process`) and chorus (`fx_chorus_stage_l`) were first validated on
the GS defaults only (Hall2 / Chorus3). GS exposes 8 of each, selected by the **reverb macro** (SysEx
`40 01 30`, types Room1/Room2/Room3/Hall1/Hall2/Plate/Delay/PanDelay) and the **chorus macro**
(`40 01 38`, Chorus1-4/FeedbackChorus/Flanger/ShortDelay/ShortDelayFB). Each was dumped live from the
running engine — `scdec revdump <out> <type>` / `scdec chodump <out> <type>` now send the macro SysEx
before snapshotting — into `tables/reverb_type_<n>_<name>.txt` and `tables/chorus_type_<n>_<name>.txt`.

**The network TOPOLOGY is identical across all 8 of each** — same struct, same code path — so the
Python transcription runs every type unchanged; only the coefficients/taps differ:
- **Reverb**: Room/Hall/Plate vary the damping (`aa8_fb`/`aac_in`), input gain, and tank feedback
  (`eef0_fb`). **Delay** and **PanDelay** are the *same tank* with the 4 allpass diffusers zeroed
  (coef `1e-05` ≈ bypass) collapsing to a single long delay tap; PanDelay reads different L/R taps
  (`2E16` vs `3C16`) for the stereo bounce, Delay reads the same tap into both (mono).
- **Chorus**: types vary `lfoInc` (rate), tap depth/base (delay), and `fbCoef` (feedback). Flanger is
  deep feedback (0.875) + small depth; ShortDelay/FB set `lfoInc=0` (static, unmodulated delay). The
  R-companion stage stays gated off (`toR=0`, `revSend=0`) for every type, so L-stage-only is correct.

`scvx_reverb.type_dump(t)` / `scvx_chorus.type_dump(t)` load a type; the sequencer auto-detects the
macro SysEx in the stream (`build_parts` -> `fx['rev_type']`/`fx['cho_type']`) or takes an explicit
`rev_type=`/`cho_type=` override, falling back to the GS default (None).

### A/B vs the real engine (per type)
Driving the real DLL with each type forced (`scdec seq … wet` + macro SysEx) and comparing:
- **Reverb — excellent across all 8**: envelope corr 0.96-0.99, STFT 0.97-0.99, tail RMS ratio ~1.0.
  (`rev/Delay` stereo-width corr reads 0.000 *correctly* — it is mono, so the L-R side signal is zero
  on both sides; PanDelay's 0.875 confirms the stereo split.)

### Chorus return-level correction (`CHO_SEND_127`: 0.786 -> 0.3428)
Isolating the engine's own chorus wet — `real_wet = engine(CC93=127) − engine(CC93=0)`, so the
deterministic dry path cancels — exposed that our chorus wet was a **consistent ~2.3× too hot** across
*all* types, including the default Chorus3 (2.36×), while the *shape* matched (env/STFT 0.94). The
original `0.786` had been set against the FULL mix of a sustained note, where wet ≪ dry made the level
~2.3× insensitive. The transcription itself is exact (verified line-for-line against the decompile:
24-bit sign-extended sawtooth LFO phase, DC-block + one-pole LPF, `D[wp]=(fb_prev·fb+lpf)·g_write`,
anti-phase taps with linear interp, `fb_prev=wet1`, L=`wet1·g_tap` / R=`wet2·g_tap`), and the coefs are
live-read — so the fix is a pure send-scalar recalibration against the engine's *measured own wet*
(in-bounds ground truth, the same method that set `REV_SEND_127`). Derived from the low-feedback types
(Chorus1-4, ShortDelay — the clean linear regime; they agree to ±2.5%, implied 0.333-0.350) -> **0.3428**.
Re-verified: low-fb wet ratio -> 1.00, feedback types 0.76-1.14 (residual is a small nonlinear
feedback-path difference, not the scalar). Float32 vs float64 identical, so it is not precision/chaos.

*Un-masked side note (out of scope, pre-existing):* with the chorus wet now correct, the full-mix
release tail on sustained **strings** reads a touch quiet on our side — a dry-timbre release-level
matter, independent of the effects, previously hidden by the too-hot wet.

## GS system Delay -- the third send effect `[confirmed -- structural model, validated 0.98-0.99]`

GS has three system send effects: reverb, chorus, and a **Delay** (SysEx macro `40 01 50`, 10 types
Delay1-4 / PanDelay1-4 / DelayToReverb / PanRepeat). This is a *separate* effect from the reverb
Delay/PanDelay TYPES (which are the reverb tank degenerated). It IS processed in GS mode -- confirmed
by a go/no-go probe (`scdec delaytest`): set the delay macro + DELAY LEVEL return + the part DELAY
SEND (SysEx `40 1x 2C`, there is no CC for delay send) and a marimba stab shows clear echo repeats.

Unlike reverb/chorus (dedicated `fx_reverb_process`/`fx_chorus_stage_l`), the system delay is **woven
into the inlined matrix + delay-line block of `fx_process_block`** -- there is no standalone
`fx_delay_process` to transcribe line-for-line. So it is a **structural model derived from the GS
parameters** and validated against the engine's own measured wet:
- The 10 macro presets are read live from `g_delay_preset_tbl @ 0x181893930` (10 types x 10 raw
  bytes: `preLpf, timeCenter, ratioL, ratioR, levelC, levelL, levelR, returnLevel, feedback, sendToReverb`).
- `timeCenter` (raw 1-115) -> ms via Roland conversion table 16; `ratioL/R` (raw 1-120) -> % via
  table 17. Center tap Tc = ms*32 samples; left tap Tl = Tc*ratioL%, right Tr = Tc*ratioR%.
- Topology: one mono feedback ring, feedback tap = center (Tc); out L = levelL*ring[Tl] + levelC*ring[Tc],
  out R = levelR*ring[Tr] + levelC*ring[Tc]. Pan types set levelC=0 with Tl=0.5Tc, Tr=1.0Tc -> the
  alternating L/R ping-pong. (`scvx_delay.py`.)

### Calibrated / measured against the engine's own wet (`wet = engine(send=127) - engine(send=0)`)
- **Tap times, L/R ratios: exact** -- measured taps align to the GS table values (ratios 50%/100% for
  pan types confirmed to the sample).
- **Fixed 60.0ms input pre-delay** -- the engine delays the send by a constant 1920 samples BEFORE the
  feedback line, on top of the table time (real first repeat lands at 60ms + timeCenter, uniformly for
  every type). The table alone put every echo 60ms early (envelope corr ~0.6); adding the pre-delay
  lifts all 10 types to ~0.99. Because the feedback line is LTI, it is applied as an output shift.
- **Feedback = (raw - 64)/64** (DELAY FEEDBACK raw 0-127, display -64..+63). Once the pre-delay is
  fixed this law gives envelope corr 0.99 (the earlier feedback sweep failed only because the pre-delay
  misalignment dominated the error).
- **Send level** `DLY_SEND_127 = 0.356` -- RMS-matched to the isolated wet at the macro-default return
  level (64); the model carries `g_ret = returnLevel/127` separately so it is return-independent.

**A/B across all 10 types**: wet RMS ratio 0.97-1.01, envelope corr 0.98-0.99, STFT 0.96.

Sequencer wiring: `build_parts` parses the delay macro (`40 01 50`) and the per-part DELAY SEND
(`40 1x 2C`, block->channel via `ChannelFromBlock`); `render_events(delay=True)` builds a mono
delay-send bus (like the reverb/chorus buses) and mixes `scvx_delay.delay()`'s wet in. Auto-detects
the macro or takes a `dly_type=` override. **Not yet modelled:** the delay->reverb feed (`sendToReverb`,
used only by the DelayToReverb preset) and individual per-param SysEx tweaks (`40 01 51..5A`); the
macro sets the whole preset, which is what songs normally use.

## TVA attack floor RESOLVED: the engine's amp attack is INSTANT (+ a smooth-attack toggle) `[confirmed]`

`compute_tva_env` used to floor the attack-from-silence to a 3 ms ramp -- an "empirical, UNRESOLVED"
value kept because it measured better than 10 ms on the level sweep. Measuring the engine's OWN per-voice
gain word at 1-sample resolution (`scdec ampramp`, reads DAT_181a1d830+(v&3)*0x40+(v>>2)*4 across
16-sample sub-blocks) RESOLVES it: on a fast attack the gain jumps **instantly** from 0 to the full
level in one control update (marimba: 0 -> 0.6287 at the first tick, then held -- no ramp, no
anti-zipper staircase). `voice_ctrl_ramp_a` DOES implement a ZOH staircase (advance the accumulator
toward target only when `(counter & mask)==0`, mask = ramp_divider[(flags>>3)&3] in {0,7,31,127}), but
on a fast attack the step reaches target in the first update, so it reads as instant. The only "attack"
is the sample's own recorded transient.

So the faithful attack floor is **0 ms** (instant), now the default (`DEFAULT_ATTACK_MS = 0.0`). The old
3 ms was a non-faithful softening; it is preserved as an opt-in **smooth-attack toggle** (`attack_ms`
on `render_note`/`render_drum_note`/`render_events`; a positive value ramps the gain over that many ms,
rounding the transient -- some listeners prefer it on percussive patches). Verified faithful/instant does
NOT click (first samples rise cleanly from 0; max first-difference 0.040 vs the 3 ms ramp's 0.038), so
the old anti-click justification does not hold.

Note: the 3 ms vs instant difference is confined to the first ~1 ms. The larger real-vs-ours marimba
attack differences are a ~4 ms note-on latency the engine has (voice-allocation delay) that our per-note
render does not model, and a broadband HF/"graininess" gap tracked separately (our TVF runs too open --
+10 dB harmonics / +19 dB noise floor above 4 kHz vs the engine; NOT the output SRC filter, which is
unity at 32 kHz, and NOT interpolation, which uses the ROM kernel).

## Sustain "graininess" SOLVED: the decay envelope was a stepped staircase `[confirmed -- SNR 80->96 dB]`

A subtle broadband "graininess" was audible in sustained/decaying patches (marimba the clearest). Root
cause: `_seg_curve` (the TVA segment shape) did a BARE 256-level lookup into `g_env_shape` indexed by
the high byte of the segment phase, with NO interpolation. Over a long decay that renders the envelope
as a **staircase** (~265-sample holds, ~4% drops here); each step is an amplitude discontinuity, and the
step edges inject a **broadband noise floor** -- the grain. Measured: sustain SNR (peak-to-noise-floor)
was 80.4 dB vs the DLL's 95.9 dB, a ~15 dB broadband excess, tilted low-frequency (the signature of an
integrated/edge-noise source, not a missing lowpass).

The engine's `env_ramp_segment` @180083a70 INTERPOLATES the fast-approach curve between adjacent
`g_env_shape` entries using the low byte of the phase -- a smooth per-sample ramp, no step edges. Adding
that interpolation to `_seg_curve` (interp `SHAPE[255-idx]`..`SHAPE[254-idx]` by `(ph&0xff)/256`) lifts
the sustain SNR to 96.5 dB, matching the DLL exactly. Faithful (matches the decompile) and validated
against the DLL's own render; no song regression.

### The dig that got here (what was RULED OUT, all by direct measurement)
This took an exhaustive elimination because the grain was at ~-120 dB and every obvious layer matched:
- **Decode is BIT-EXACT.** New `scdec predtrace` reads the engine's own ADPCM predictor accumulator
  (voice state +0x40) sample-by-sample; it equals our `cumsum(delta<<(scale+10))` exactly at every
  position (pos 3/66/97 verified). The whole block-FP DPCM codec is perfect -- not the decode.
- **Interpolation kernel**: ours already uses the ROM 4-tap kernel; a high-quality windowed-sinc makes
  the HF WORSE (our kernel is a milder lowpass), so imaging/aliasing is not it.
- **Fixed-point vs float phase**: identical noise floor -- not the phase.
- **Output SRC filter** (`tg_output_filter`): `scdec outfilt` shows ratio 1.0 (unity, transparent) at
  32 kHz (0.7256 = 32000/44100 at 44.1 k) -- ruled out.
- **TVF, loop seam, loop period/pitch, predictor retention (continuous loop), level**: all identical
  or ruled out. The raw decoded loop tiled at integer length is clean (97.9 dB); resampling and even
  the decaying-but-CONSTANT-amp render stayed clean (96 dB) -- isolating the stepped envelope as the
  sole source.

New diagnostic decoder modes from this dig: `ampramp` (per-voice gain word at 1-sample res -> the
attack is an instant jump, not a ramp), `outfilt` (SRC state), `sampstate` (sampler pointers/bytes),
`predtrace` (predictor accumulator vs our cumsum).

## Pitch KEY-FOLLOW: block[0x13] is the follow amount, not just the g_kf_pitch row `[confirmed]`

The onestop.mid Seashore SFX came out as an octaves-too-low bass rumble. Root cause: `base_pitch_ms`
computed the pitch key as `note + transpose` (100% key-follow) always, using block[0x13] ONLY to pick
the g_kf_pitch row. But block[0x13] is the **pitch key-follow amount**. `multisample_key_zone`
@180003210 reads it: 0x40 = 0% follow (key = key-center), 0x4a = 100% (key = note, special-cased), and
in between the note's distance from the key-center is scaled by a LUT (`DAT_1800935c0`) before it
drives pitch. With the *2 in the formula, kf_scale = LUT[block[0x13]-0x40]*2/65536 = (block[0x13]-0x40)
*0.1 -- a clean 0%/10%/20%/.../100% ladder. Seashore is 0x42 = **20%**: note 33 should play as
effective key ~54 (ratio 0.69), not key 33 (ratio 0.165) which shoved the surf noise 31 st down into
the bass and killed the HF hiss.

Fix: `keyfollow_key(block[0x13], note, key-center)` -> (effective key, fractional crossfade weight),
fed into `base_pitch_ms` (which adds the weight and still applies the g_kf_pitch row). **Full-follow
patches (0x13=0x4a) are byte-for-byte unchanged** -- keyfollow_key returns (note+transpose, 0) -- so
the 13-voice pitch validation and the song A/Bs are unaffected; only reduced-follow patches (SFX, some
pads/leads) move, toward the DLL. Verified: Seashore note 33 now matches the DLL across the spectrum
(was -69/-122 dB LF/HF, now -81/-98 vs real -81/-106); an octave sweep tracks at the measured ~20%.

## TVF envelope: segments 2/3 used the wrong velocity level-scale (harpsichord "cut-off")

Symptom: the harpsichord (and other patches) darkened and died noticeably earlier than the DLL in the
sustain, worst on high notes -- the isolated onestop harpsichord (ch3, prog 6, 93.2-119.1s) "seemed to
cut off." Chased top-down with the engine's own state as ground truth:

- **TVA amplitude env is correct.** `scdec ampramp` (per-voice gain word) through the note-84 decay:
  our `compute_tva_env` tracks it at a constant 0.94 ratio the whole way down -- decay RATE exact, a
  uniform ~6% level offset only. Not the cause.
- **Sample is a clean loop.** note-84 wave = 325-sample loop, flat RMS, ~zero per-pass drift. No decay
  hiding in the sample.
- **TVF cutoff env closes too fast.** `scdec tvftrace` (field `+0xcc`, and the internal env level
  `+0xec`) inverted through the warp table to linear cut15: segment 1 matched EXACTLY, but segments 2
  and 3 descended **1.45x too steeply** in both notes measured (84 and 48). On note 84 the fundamental
  is ~1046 Hz, so once our cutoff dived under it near 1 s the note darkened/died while the DLL was still
  ringing (audio death gap: note 84 was 0.33 s early, note 72 0.13 s).

Root cause, straight from `tvf_compute_env_rates` @1800616f0: the TVF segment rates use **two** velocity
level-scale factors, and we were using one.
    DAT_181a1f5ba = env_level_scale(vel, block[0x4b])  -> segments 0,1        (lines 40506 / 40527)
    DAT_181a1f5bc = env_level_scale(vel, block[0x4c])  -> segments 2,3 + rel  (40550 / 40564 / 40583)
`compute_tvf_env` applied `env_level_scale(vel, block[0x4b])` to all four segments. On Harpsichord
block[0x4c]=0x3c gives level-scale **370** vs block[0x4b]=0x40's **256** -- exactly the measured 1.445x.
This mirrors the TVA, which already splits its two level factors (b0 from 0x69 for segs 0,1; b2 from
0x6a for segs 2,3 + release) -- the TVF port just missed the second one.

Fix: `bv01 = env_level_scale(vel, block[0x4b])` for segments 0,1; `bv23 = env_level_scale(vel,
block[0x4c])` for segments 2,3 and the release. No new tables.

Validation (`tvftrace`, map 4 == default GS for prog 6, verified identical): the fixed cut15 now tracks
the engine's `+0xcc` within ~2% in Hz across the whole 2.6 s decay (note 84 @1.0 s: 1068 Hz vs engine
1083 Hz, was 740 Hz). Audio death gap note 84 0.33 s -> 0.038 s; note 72 0.13 s -> 0.045 s. dB decay
tail: the 6-7 dB gap by -40 dB collapsed to <1 dB. Full-song A/B unchanged (short notes dominate the
aggregate; the fix is on exposed sustained notes).

Residual (small): a roughly constant ~600-1200-unit offset in the TVF env LEVEL (`+0xec`) -- our
peak/target depth runs slightly high; the warp compresses it to <2% in Hz, inaudible.

### TVF release rate: use the release row/mod bytes (0x47/0x49), not the main ones

The release segment (block[0x43]) has its OWN rate key-follow: `tvf_compute_env_rates` line 40366 sets
`DAT_181a1f5be = env_rate_scale(g_kf_tvfrate2[block[0x47]*0x80 + key] - 0x80, block[0x49])`, distinct
from the main segments' `b8` (block 0x46/0x48). We were reusing `b8` for the release too, so on the
Harpsichord the release ran **12399 units/s vs the DLL's ~10440** (18% too fast) -- measured directly
with a note-off tracer added to `scdec tvftrace` (arg 11 = note-off fraction; reads the `+0xec` env
level through the release). Reading the release bytes fixed it to **10425 units/s**, matching the DLL.

`g_kf_tvfrate2` is a separate Ghidra symbol but its data is contiguous with `g_kf_tvfrate` and **row 0
is byte-identical** (both the default linear ramp `(128 + 2*key) mod 256`), so the existing KFTVFR
table reproduces it wherever `block[0x47] == 0` -- which is **98% of partials** (only 152 / 8190 have a
non-zero release-rate row). Those ~2% would need the distinct `g_kf_tvfrate2` rows exported if a
release mismatch ever shows; none observed. NB the DLL's `+0xec` freezes ~1.3 s post-note-off because
the TVA has already silenced the voice (voice-death artifact), so the release only matters while the
note is still audible, where the slope now matches.

New tool: `scdec dumpmem <VAhex> <count> <out>` reads N bytes from the loaded image (runtime VA).

### Scope note: insertion EFX is OUT of this codebase

The 66-algorithm insertion EFX subsystem (separate from the GS send effects, which ARE done) is
deliberately **not** implemented here -- it belongs downstream in the concrete cross-platform engine,
not the reference model. This repo specifies the core voice + send-FX path and the DLL table/ROM
layout; a downstream implementation adds insertion EFX on top.

## The tone table is 2363 records, not 2048 `[confirmed — A/B against the DLL]`

`tables/tone_a.bin` was sliced at `0x80000` — a round 2048 records — and that was 315 records short.
Every drum key pointing past 2047 resolved to nothing: **484 keys across the 47 kit records**, on both
drum map rows.

**Nothing in the engine bounds this table.** `tone_lookup` @`1800026d0` tests only `tone# < 0x4000`
and indexes `g_tone_table_melodic` directly, so the length is not a code fact — it is a layout fact,
and three measurements agree on it:

- Drum kits reference tones up to **2353** (`g_drum_kits` tone plane, `+0x000`).
- Records read as tone records — 12-byte Latin-1 name, level byte at `+0x0c` in range, the same
  `01 00 00` at `+0x0d` — through **2362**. Index 2363 is not one: its name field is `00 04 7F 54`.
- The next known object, `g_ramp_exp_tbl`, starts at file offset `0x1985420`, leaving room for no
  more. 2363 records end at `0x1985310`.

So the region is `0x18f1810 + 0x93b00` = **604,928 bytes**.

**The disputed records are named, and named as what the kits use them for.** 2048 `Req_tik`, 2049
`Tabla_Te`, 2071 `Standard KK1`, 2362 `ConcertBD Mt` — sitting on exactly the keys a tabla or a kick
belongs on. Random bytes do not do that.

**Verified against the DLL, not against the arithmetic.** `scdec drumnote 49 57 100 2` — program 49,
note 57 on ch10, whose key is tone 2049 — sounds at **peak 0.118** through the real engine, and
rendered **silent** in the downstream C# engine on the short slice. With the region widened, the same
hit against that capture: envelope correlation **1.000**, decay to −20 dB **45 ms vs 45 ms**, and
every octave band from 40 Hz to 16 kHz within **0.4 dB** (the 0.1–0.2 dB floor is the normalisation
offset between the two harnesses).

**Blast radius.** The keys past 2047 belong to the ethnic and SFX kits rather than the GM ones, so
most drum renders are unaffected: `onestop.mid`'s drum part is bit-identical before and after on map
row 0. On row 1, where program 0 resolves to kit 38 and its two kicks live at 2070 and 2071, it moves
**115% RMS**.

Found by ear, downstream, by a person saying "drum b has no kick drum" — after the silence had been
measured, explained, and pronounced correct.

## The wave descriptor table is 4259 records, not 4096 `[confirmed — three oracles]`

The same round-number mistake as the tone table, one level down. `tables/wavedesc_a.bin` was sliced at
`4096*0x16` and **163 zones** — across the multisamples a defined tone actually reaches — pointed past
the end and resolved to nothing.

The bound is exact rather than inferred:

- Those multisamples name waves up to **4258**.
- 4259 records from `0x181897b40` end at `0x18189ad942`, and `g_drum_kits` begins at `0x18189ad950`.
  Fourteen bytes of padding, and room for not one more record.

**The third oracle arrived unprompted.** `wave_directory_full.csv` — the waves the real engine was
captured selecting — matches **2022 of 2022** forward waves against the widened table, where it
matched 2014 against the short one. Those eight had been written off downstream as empty-loop
one-shots reached through the drum tone table. They were nothing of the kind: they were waves 4096
and up, off the end of the slice.

Confirmed audibly too, on a key the shortfall silenced. Program 53 note 91 on the drum part — `Hand
Clap`, wave 4097 — sounds at peak 0.118 through the DLL and rendered silent downstream. Widened, the
same hit against that capture: envelope correlation **1.000**, decay to −20 dB **100 ms vs 100 ms**,
every band within 1 dB where the clap has energy.

`multisample_a.bin` is **not** short. Nothing reaches past multisample 1174 of its 2048, so that
round number is coincidence rather than a third instance — worth stating, so all three do not read as
the same guess.

### Two things this uncovers rather than settles

**~~The sampler has no reverse path.~~ Solved — see "Reverse waves" at the end.** Of the 198 drum
voices the two widened tables make reachable, **167 are reverse waves** (`flags` bit 2) and they
rendered silence. The guess recorded here — that their descriptors describe a region running back
from the loop point, needing an alternate register setup — was wrong on both counts. The descriptors
are ordinary; the partial was being skipped before anything read them.

**`g_wave_desc_table_b` and `_g_multisample_table_b` are aliases, in this build.** They are selected
against a tone# threshold (`g_tone_set_header_b + 0xe`) in `multisample_select_wave` @`180003420`, so
it looks like a second table set — but init @`180086d00` does `_g_multisample_table_b =
g_multisample_table_a` and `g_wave_desc_table_b = g_wave_desc_table_a`, and neither is assigned
anywhere else. The branch chooses between two pointers to the same memory. Anyone reading a wave
index above 4095 as "that must be table B" will be wrong here.

## Reverse waves — the data is simply read backwards `[confirmed — A/B against the DLL]`

The 218 descriptors carrying `flags` bit 2 rendered **silence** downstream, and the reason was not the
sampler at all: both renderers held an explicit `if (descriptor.Reverse) continue;`, added when the
reference model skipped them too. The partial was dropped before anything tried to play it. Worth
recording because it is the failure mode that hides — a wrong *sound* gets noticed, and a missing one
looks like data the module lacks.

**What they actually are.** No alternate register setup is needed to sound one. The static descriptor
gives an ordinary `[loop, start]` data region — measured across the whole table, **all 218 reverse
descriptors have `loop <= end`**, exactly like the 4 041 forward ones — and the wave is that data read
from the far end back to the near one. The `loop_start > end` the harness captures for a reverse wave
is the *runtime* register layout, which is the engine expressing "start high, run down", not a
different region.

Always a one-shot. 202 of the 218 already collapse `end` onto `start` statically and the engine
reconfigures the other 16 to match, which is why the loop geometry in the descriptor decides nothing
here.

**Implementation note for the downstream engine:** decoding forward and reversing the finished buffer
is equivalent to walking the delta stream backwards, and avoids the seam question entirely — the
predictor is integrated in its natural direction and only the output order changes. There is no loop,
so the DC-carry problem that makes the engine's own backwards walk delicate never arises.

**Verified against the DLL on two keys**, program 57 note 38 (`Rev.PowerK1`) and program 53 note 71
(`Rev.Cymbal2`):

| | Rev.PowerK1 | Rev.Cymbal2 |
|---|---|---|
| envelope correlation vs capture | **+1.0000** | **+0.9998** |
| same, with our output time-reversed | −0.5601 | −0.7463 |
| peak position, real / ours | 97.22% / 97.22% | 85.62% / 85.62% |

The second row is the control and it is the one that matters. A magnitude spectrum is very nearly
invariant under time reversal — every octave band from 40 Hz to 16 kHz matched to 0.0 dB whichever way
round the samples went — so the spectrum cannot tell a correct implementation from a backwards one.
The envelope can, and it separates the two by 1.6 in correlation.

`scdec drumnote 57 38` also shows the engine holding `amp` **constant at 0.768860** across the whole
hit while `tva_lvl` decays gently from 12773 to 11783. The swell is in the sample data, not in the
envelope — which is the clue that the fix belonged in the sampler and not in the TVA.

## TVP — the pitch-envelope runtime machine, recovered `[confirmed from binary + decompile]`

The earlier pitch-envelope work (see "Broadening the pitch-envelope test set" above) derived the
segment law from measurement and left one open question: the stage-advance handlers reached through
`PTR_1819a17c8`, whose targets were **absent from the decompile**. Those targets have now been
recovered by disassembling the pointer table's entries straight from the DLL image, and together
with the control-tick driver they give the complete runtime machine. Everything below is engine
behaviour restated from that code; nothing here is measurement-inferred any more.

### The stage machine

The per-voice pitch-envelope state is **two ramp structs**, A at `voice+0x5c` and B at `voice+0x74`
(each: stage byte, rate-scale byte, per-sample interp word, shape word `0x4000`, rate word, start
`i32`, target `i32`, output `i32`, 16-bit phase + carry). The engine copies the whole voice control
block into a scratch area each control tick, steps it, and copies it back; ramp A's output is the
`voice+0x6c` value every trace in this project measured.

- **Stage advance.** At the *start* of a tick, if ramp A's phase word reads `0xffff`, the engine
  zeroes it, steps the stage byte through a 6-byte next-stage map `[1, 2, 3, 4, 4, 4]`, and calls
  the handler for the new stage from a 5-entry pointer table (`0x1819a17c8`). Stages 0 and 4 point
  at a no-op; stages 1–3 point at three tiny loaders (`0x180083870/83800/83790` — these are the
  functions Ghidra never disassembled). Stage 4 is terminal: the ramp-step routine returns
  immediately when the stage byte is 4, so the level holds forever.
- **The handlers load the next segment fresh.** Handler *n* sets: start ← the ramp's current
  *target* (not its output), target ← `voice+0x210/0x214/0x218` (the levels the note-on compute
  wrote: targets 2, 3, and 4 — the last being the unbiased base pitch), rate word ← from a stored
  segment *time* `voice+0x204/0x206/0x208` converted **at segment start** by the familiar
  `T < 11 ? 0xffff : 0xa0000/T`, shape word ← `0x4000`, and the per-sample interp word ←
  `g_env_startphase[min(T, 10)]`. Segment 0's rate word is the only one precomputed at note-on
  (`voice+0x62`); segments 1–3 store milliseconds and convert lazily. This is the mechanism behind
  the measured "next segment starts fresh with no phase carry" law.
- **Rate-scale byte.** Each ramp carries a scale byte `c`: the effective rate is
  `rate · (0x10000 − ((c<<9)|(c>>7)) − 1) >> 16`, i.e. roughly `rate · (1 − c/128)`. The value
  `0x7f` is used as a **park marker** — a parked ramp is not stepped at all.
- **Ramp interpolation.** Output = start + (target − start) · phase/0x10000, with the
  (target − start) delta clamped to ±`0x1f018` first. Phase accumulates `rate × block-speed` per
  tick with a carry word for multi-block catch-up; hitting `0xffff` exactly snaps output to target.

### Release is a second ramp and a crossfade — and the "enable flag" is the release rate

At note-on, ramp B is initialized **parked** (rate-scale `0x7f`) with start = output = **absolute
zero pitch**, target = the release level (`voice+0x80`), phase = 0, and rate word = the release
rate (`voice+0x7a`) computed by the same `T<11` rule. What the earlier work called the envelope's
"enable flag at voice+0x7a" is literally this release rate word — a disabled envelope zeroes it,
and the block update skips all stepping when it is zero.

At note-off the engine:

1. parks ramp A (rate-scale ← `0x7f`) — in the default configuration;
2. activates ramp B by writing its rate-scale byte from **`part+0x462`** (default 0 = full rate);
3. thereafter outputs `A.out · (0xffff − phaseB)/0x10000 + B.out` each tick.

Since B interpolates 0 → release-level by the same phase that weights the blend, the default
(A parked at level `L`) collapses algebraically to `L·(1−t) + release·t` — **exactly the linear
splice the reference model already implements and validated to ≤1 mst**. The refinements the
dual-ramp form adds are the non-default cases:

- `part+0x462 ≥ 0x40` — the pitch release is **not engaged at all**; the envelope keeps running
  through note-off. Values 1–0x3f scale the release rate down by the rate-scale formula.
- On the voice-steal path (a flag at `voice+8`), ramp A is *not* parked: the still-running envelope
  crossfades into the release ramp.
- When B's phase reaches `0xffff` its stage is set to 4 and the output stays at the release level.

### `block[0x00]` / `block[0x01]` — the envelope hold clock `[confirmed]`

*(An earlier draft of this section called this a periodic "retrigger clock". Tracing the arming and
firing code settled it: the clock **fires once and disarms** — it is a delayed envelope start, not a
tremolo. The tremolo tones carry their tremolo in the recorded sample.)*

Two previously undocumented bytes at the head of the 0x6e partial block arm a one-shot clock at
note-on (16-bit counter at `voice+0xfa`, step word `0xffff / period` at `voice+0xfc`). **While the
clock runs, the voice renders nothing at all** — the per-tick driver counts the clock instead of
stepping TVA/TVF/pitch/LFO, the amp word stays at zero, and the **wave's read position stays
frozen too** (measured live: the sampler-state position sits still until the clock fires — an
earlier draft here guessed the sample ran underneath, which was wrong). When the counter wraps, the
engine snaps the output ramps to the stored note-on values (`voice_env_retrigger`), disarms, and
the whole voice — envelopes *and* wave — simply starts. An armed voice is exactly the same voice
time-shifted.

- **Low 7 bits ≠ 0: a delayed start.** `period_ticks = (level_scale(velocity, block[0x01]) ·
  g_rate_curve[clamp(part+0x45b + part+0x44b + (block[0]&0x7f) − 0x80)] >> 8) / 10`. Both part
  bytes default to `0x40`, so the index is normally just the byte itself, and `g_rate_curve` is in
  milliseconds: `Piano+Choir1`'s choir layer enters 3 ticks (30 ms) late, `Puff Organ`'s puff 5.
  Values 1–2 compute to zero ticks and never arm (data carried from hardware, inert at the 100 Hz
  tick). A period of zero stores the disarmed sentinel `0xffff` — the default path for
  `block[0x00] = 0`. **Note-off while the delay runs kills the voice before it ever sounds.**
- **`block[0x00] == 0xff`: a key-off layer.** Armed with step 0, so the clock never fires on its
  own — the voice stays silent for the whole held note, and **note-off is what fires it**: the
  engine disarms, redraws the random detune (`tvf_env_prep`), snaps the output ramps, and the
  layer sounds — envelopes running their normal course from the top (the note-off was consumed
  arming the fire, so no release is pending; the voice ends by sample end or envelope end). That
  is what the `.o` suffix means: `Harpsi.o`'s 0xff partial is the harpsichord's **key-off clack**,
  measured firing on the control tick after note-off with its wave starting from position ~0 after
  600 ms of being held. The 10 partials: `Harpsi.o`, `Clav.o`, `Organ o`, `Nylon Gt.o`,
  `MandolinTrem`, `Aqua`, `Biwa 3`. (When the note-group queue check fails the engine kills the
  armed voice instead of firing it — condition observed in code, not yet exercised.)
- **Bit 7 sets the one-shot flag (`voice+5`)** consulted by the normal (disarmed) note-off path to
  skip engaging the envelope releases; in the shipped tone set bit 7 only ever appears as part of
  `0xff`. 92 partials carry a nonzero low field in total.

The suspended state is also a piece of the old "what makes the engine stop advancing a pitch
envelope" question — an armed clock freezes all stepping. It does **not** explain the two recorded
outliers: Mellow Gt. and Bird 2 both have `block[0x00] = 0`. Their early halt therefore lives in
the alt-articulation / voice-pair kill path (`voice+0x120`/`voice+0x188`), which remains open — the
practical impact is unchanged (it only matters once a voice has stopped contributing).

### `block[0x1a]` — random start-pitch jitter `[confirmed]`

Another previously undocumented byte. At note-on (after the five bias levels are computed, and
**even when the envelope depth is zero**), the engine draws one value `r` from the shared PRNG and
offsets the envelope's *start level*:

```
if bit14 of r is clear:  start += ((((r & 0x7fff) >> 7) · d + 0x80) >> 8) · 10
else:                    start −= ((((uint16)(−2·r) >> 8) · d + 0x80) >> 8) · 10,  clamped ≥ 0
```

with `d = block[0x1a]`. Note the asymmetry: the positive branch takes a **7-bit** magnitude slice
(bit 14 is already known clear, bit 15 is masked off) while the negative branch takes an 8-bit one,
so the jitter ranges over roughly `[−10·d, +5·d]` milli-semitones, biased flat. It is applied to
the start level only — with an active envelope it fades over segment 0; with a disabled envelope the level is
never stepped, so it is a per-note **constant random detune**. 19 partials use it (`d` = 5 or 10 →
±50 or ±100 mst): `Jazz Bass 2`, `Fretless Bs2`, `Octave Brass`, `Oct SynBrass`/`2`, `Soft Brass`,
`Velo Brass 1`, `TB Lead`, `SynthBrass1`, `Poly Brass`, `Quack Brass`, and friends — the classic
"analog feel" detune on layered brass/bass patches.

### The PRNG, exactly

`prng_lfsr` keeps two 16-bit registers, **seeded at engine reset to `0xEFA6` and `0x9C23`** (the
reset routine that also installs the MIDI-drain/scheduler pointers and resets the parameter
tables writes both constants):

- R1 (`0xEFA6`): Fibonacci LFSR shifting right, new bit15 ← bit5 ⊕ bit15.
- R2 (`0x9C23`): shifts left; inserted bit0 ← bit9 when bit2 is clear, else ¬bit13.
- Output: one step of each, then `R2' ^ R1'`.

So the engine's "random" detune sequence is **deterministic from engine reset**; only the order in
which voices consume draws varies with polyphony. (This is the same shared generator the random LFO
waveforms use, which is why those were left returning zero in the reference — a *sequence* cannot
be aligned under polyphony, but the note-on jitter is one draw with the correct distribution either
way.)

### Also recovered in passing

- **Portamento glide** is a separate additive term (`voice+0x8c`): an offset that decays linearly
  to zero by `g_porta_step[part-portamento-time-byte] · block-speed` per tick (step table at
  `0x1819a7800`), summed into the absolute pitch inside the same `[0, 0x1f018]` clamp.
- **Part fine pitch** (`part+0x3db`, `0x80`-centred) is scaled by a per-key table at `0x1819a7900`
  before joining the pitch sum — a key-scaled part detune.
- A second random subsystem (`g_pitch_split_coarse/fine`, indexed by `part+0x3dd` or randomly per
  note-group when that byte is 0) writes a coarse/fine pitch-word pair at `voice+0xf4/0xf6`,
  redrawn when a suspended one-shot advances to a queued alt-articulation entry at note-off; its
  consumer is the per-sample pitch-word path. Documented as far as verified; the exact consumer
  remains to be pinned.

*Method: pointer-table entries read from the DLL image at `0x1819a17c8` (file `0x19a07c8`) and
disassembled directly — three 0x70-byte loaders Ghidra's recursive descent never reached because
they are only referenced through data. The TVA envelope has an identical machine (its own 5-entry
table at `0x1819a2408`, same next-stage map) and the TVF a third; the three loaders' TVF/TVA twins
sit at `0x1800838e0/83960/839e0`.*

## The hold clock and start jitter, verified against the DLL — under Wine `[confirmed]`

The DLL oracle does not need Windows: `scdec` is a console app and `SCCore.dll` is pure compute, so
the whole tracing harness runs under Wine (verified with wine 11.14; publish the decoder
self-contained for `win-x64` and pass the DLL as a `Z:\…` path). A `postrace` mode was added to
`scdec` for this pass: per-control-tick sampler read positions for every active voice, with an
optional mid-note note-off.

**Start jitter (`block[0x1a]`) — bit-exact against the modelled PRNG.** Jazz Bass 2 (tone 246,
depth 5, both partials), map 3 bank 3 program 33:

| measurement | result |
|---|---|
| `voice+0x64` vs the no-jitter prediction (base + envelope start) | Δ = **0** and **+10** mst on the two partials |
| control (Piano 1, no jitter byte) | Δ = 0 (−1 mst base residual, known) |
| repeat run, fresh process | identical Δ — deterministic from engine reset |
| velocity 32 instead of 100 | identical Δ — jitter is velocity-independent |
| note 57 instead of 45 | identical Δ riding on the shifted base |
| the pair (0, +10) in the modelled LFSR jitter sequence | draws **3 and 4** from the reset seeds |

So the LFSR algorithm, the `0xEFA6/0x9C23` seeds, and the jitter formula are all confirmed
bit-exact, modulo a fixed **3-draw preamble** consumed by engine initialisation before the first
note. (Sequence from reset, depth 5: −20, −20, 0, **0, +10**, −10, +20, 0, …)

**Delayed start (Piano+Choir1, tone 8).** The choir partial's voice shows amp exactly 0.000000 and
its envelope level frozen at 13476 for precisely **3 control ticks**, first movement on tick 4 —
matching the predicted `g_rate_curve[4] = 31 ms → 3 ticks` hold. Its **sampler position is frozen
at 3** for those ticks and starts advancing at tick 4: the wave is delayed along with the
envelopes.

**Key-off layer (Harpsi.o, tone 44).** The 0xff partial's voice: amp 0.000000 and sampler position
frozen at 3 for the entire 600 ms held note; on the control tick after note-off the position starts
advancing from ~0, the amp bursts, and the envelope level begins stepping from its note-on value —
the harpsichord's key-off clack, played whole no matter how long the note was held. The audible
harpsichord is the tone's *other*, ordinary partial, which releases normally at the same moment.

The reimplementation's hold model was corrected from these measurements (armed voice = pure
silence, wave parked, fire = time-shift; 0xff = fires at note-off with no release pending) — see
the sibling repo's `EnvelopeMachine.HoldSamples` / `PartialVoice` / `NoteRenderer.RenderPartial`.

## `part+0x462` is the damper pedal — and the SC pianos have half-damper `[confirmed]`

The TVP release investigation left `part+0x462` as "a release-rate modify byte, default 0". It is
the **CC64 damper value**, and the odd-looking release semantics are a working **half-damper
implementation** reserved for the piano tones.

**Recovering the CC dispatch.** The engine's per-CC handlers live in a 187-entry pointer table at
VA `0x18199fb30` (file `0x199eb30`): slots 0–127 indexed by controller number, the rest carrying
channel-level events. Ghidra recovered only the handlers that other code references; the rest —
including CC64 — are reachable only through the table and are absent from the decompile, the same
failure mode as the pitch-envelope stage loaders. (Cross-checks: slot 120 = the named All-Sound-Off
handler, slot 121 = Reset All Controllers — which is what zeroes `+0x462` — and the recovered
slot-11 handler writes `part+0x464`, the expression byte that same reset restores to `0x7f`.)

**The CC64 handler** (`0x180065e50`, disassembled from the image): for every part receiving on the
channel (Rx gates `0x820` in `part+0x3d6`),

```
if (part+0x24c bit 2)  part+0x462 = value            half-damper: raw 0..127
else                   part+0x462 = value > 0x3f ? 0x7f : 0     binary pedal
```

**How it plays out at note-off** (per the release-engagement logic already documented above):

- `part+0x462 ≥ 0x40` — the envelope releases are not engaged and a key-off (0xff) layer does not
  fire: the note sustains. The bookkeeping boolean at `part+0x46f` (pedal ≥ 0x40) queues the
  note-off for replay at pedal-up, so the release finally engages with whatever the pedal value is
  *then*.
- `1 ≤ part+0x462 ≤ 0x3f` — only reachable on a half-damper part — the release engages, but the
  value lands in release ramp B's rate-scale byte: rate × `(0x10000 − (v<<9|v>>7) − 1) >> 16`,
  roughly `1 − v/128`. A half-pressed pedal makes the release proportionally longer — real
  half-damper behaviour.
- `0` — normal full-rate release.

**Half-damper is a per-tone property.** `part+0x24c` is copied on every tone select from **tone
header byte `+0x0d`**, a flags byte (values 0/1/5/6/7 across the table; bit 2 = half-damper).
Exactly **57 of 2363 tones** set bit 2 — the piano family, wall to wall: `Piano 1/2/3`,
`UprightPiano`, `Mild/Pop/Rock/Dance Piano`, `European Pf`, `Piano + Str.`, `Piano+Choir`,
`EG+Rhodes`, and friends. Half-pedal on the Sound Canvas is a piano feature, as on the hardware.

*Downstream note: the reimplementation models CC64 as a boolean with note-off replay, which is
exact for the other 2306 tones; the missing refinement is routing the raw pedal value into the
release-rate scale on the 57 half-damper tones.*

## CC66 sostenuto — captured-note bitmap and deferred release `[confirmed]`

The sostenuto handler (`0x1800661a0`, another table-only function recovered from the image) is a
textbook sostenuto, binary-only — it reads nothing but bit 6 of the value, so there is no
"half-sostenuto". Parts receive it through Rx mask `0x880` in `part+0x3d6`, which alongside hold's
`0x820` and expression's `0x810` pins the Rx-switch bits: `0x800` = Rx.CONTROL, `0x80` =
Rx.SOSTENUTO, `0x20` = Rx.HOLD-1, `0x10` = Rx.EXPRESSION.

- **Pedal down:** for every *sounding* note-group (state byte `node+0x30` == 1) on the part's list
  (`part+0x270`, chained at `+0x20`): set the note's bit in a **128-bit captured-note bitmap at
  `part+0x260`** (8×u16, indexed by `node+0x36`, the note number) and set the capture flag,
  `node+0x34` bit 0.
- **Note-off while captured:** `part_notes_note_off` moves the group's state 1 → 2 as usual but
  marks the voices released (`voice+0x16d = 1`) **only when the capture flag is clear** — a
  captured note's release is deferred, exactly like the damper's but per-note.
- **Pedal up:** clear each group's capture flag; for groups already in state 2 (note-off arrived
  during capture), set `voice+0x16d = 1` on the whole voice chain — the standard release
  engagement, which therefore composes with everything above it: the release rates take the
  *damper* value in `part+0x462` at that moment (half-pedal scaling included), a still-down CC64
  keeps the note sustaining regardless, and a key-off (0xff) layer fires. Finally the bitmap at
  `part+0x260/0x268` is zeroed wholesale.

Nothing sostenuto-specific reaches the envelopes: capture only gates *when* `voice+0x16d` is set,
and everything downstream is the already-documented release machinery.

## Drum pitch NRPN "range doubling" is per-tone key-follow, not an SC-55 mode `[confirmed]`

A long-standing observation is that the drum-pitch NRPN (`18h`, *Drum Instrument Pitch Coarse*)
covers **twice as many semitones in SC-55 mode**. Measured against the DLL under Wine, the effect
is real and reproducible — and its cause is not the tone map.

**Measurement** (`scdec drumprobe <note> <map> 24 <value> [prog]`, new mode: strikes the note,
sends the NRPN, restrikes, and reads the part's live per-note planes plus each voice's absolute
pitch `voice+0x64`). Note 38, NRPN value `0x4C` = plane +12:

| map | prog 0 (Standard) | prog 24 (Electronic) |
|---|---|---|
| 1 — SC-55   | 60000 → **72000** (+12 st) | 60000 → **72000** (+12 st) |
| 2 — SC-88   | 60000 → 66000 (+6 st) | 60000 → **72000** (+12 st) |
| 3 — SC-88Pro| 60000 → 66000 (+6 st) | 60000 → **72000** (+12 st) |
| 4 — SC-8820 | 60000 → 66000 (+6 st) | 60000 → **72000** (+12 st) |

Linear in the value throughout (+4 → +4000/+2000 mst, +32 → +32000/+16000), and the stored plane
byte is identical in every case (60 → 72), so nothing about the *handler* differs.

**The Electronic kit doubles the range in every map**, which rules out a mode switch. Resolving the
kit each cell actually used and reading the tone behind note 38 settles it:

| map | kit | tone | `block[0x13]` pitch key-follow |
|---|---|---|---|
| 1 | Fat Snare | 2330 | `0x4a` = **100%** |
| 1, 2, 3, 4 (prog 24) | Elec. Snare | 1840 | `0x4a` = **100%** |
| 2 | Std.1 Snare1 | 1821 | `0x45` = 50% |
| 3 | Standard SN1 | 1826 | `0x45` = 50% |
| 4 | 85St Snare2 | 1776 | `0x45` = 50% |

Perfect correlation, with no exceptions in the set. **The NRPN sets the note's *key*, and the tone's
own pitch key-follow decides how much pitch a key step buys** — the same `block[0x13]` ladder
already documented for melodic notes (0x40 = 0%, 0x4a = 100%, 10% per step, via
`multisample_key_zone`). A 100%-follow tone moves a full semitone per plane unit; a 50%-follow tone
moves half of one. SC-55 mode "has twice the range" only because the SC-55 kits happen to use
100%-follow snares where the later standard kits use 50%-follow ones.

**There is no map-dependent scaling in the CC/NRPN handlers at all.** The recovered handlers
(`cc64_hold_damper`, `cc66_sostenuto`, `cc67_soft_pedal`, `cc11_expression`, `nrpn_apply`) contain
no mode or tone-map conditional; the only map involvement anywhere near this path is kit
*selection* — `nrpn_apply` case `0x18` reads the kit's default plane value through the
bank-row/program-column LUTs, adds `value − 0x40`, clamps to 0..0x7f, and stores it in the part's
per-note map at `+0x180`. The map picks the kit; the kit picks the tone; the tone carries the
key-follow.

**Consequence for a reimplementation:** modelling drum coarse pitch as a fixed half-semitone per
unit (`2^((plane−60)/24)`) is right only for 50%-follow tones. The general rule is the melodic one —
take the plane value as the key and run the ordinary base-pitch chain (`keyfollow_key` +
`g_kf_pitch`) with the partial's own key-follow byte. That reproduces both columns above exactly:
100% gives `plane*1000`, 50% gives centre + half the distance (60 → 66 for plane 72).

## Portamento — the glide term, measured `[confirmed]`

The pitch sum carries a fourth term besides base, envelope and LFO: a **portamento glide offset** at
`voice+0x8c`, added inside the same `[0, 0x1f018]` clamp and walked to zero at a fixed rate. New
`scdec portatrace` mode traces it per control tick.

**Controllers** (all recovered from the CC dispatch table; Rx gate `0x840` = CONTROL + PORTAMENTO):

| CC | handler | effect |
|---|---|---|
| 5 — portamento time | `0x180066040` | `part+0x463 = value`; also sets the armed bit (`part+0x08` bit 2) if portamento is already on |
| 65 — portamento on/off | `0x180065fe0` | ≥ 0x40 sets `part+0x08` bits 1 and 2; below clears both |
| 84 — portamento control | `0x180065ef0` | `part+0x24d = value` — the source key for exactly one note (Rx gate `0x800` only) |

**The glide.** At note-on the offset is `sourcePitch − thisNote'sBasePitch`, so the voice sounds at
the source and climbs (or falls) into tune. Each control tick it moves toward zero by
`g_porta_step[part+0x463]`, a 128-entry u16 table at `0x1819a7800` — new
`tables/porta_step_7800.bin`. The glide is therefore **linear in pitch, not in frequency**: constant
milli-semitones per tick, so an octave takes the same time wherever it starts. Measured against the
DLL at three time bytes, exact:

| CC5 time | table | measured decay per tick |
|---|---|---|
| 32 | 928 | −23109 → −22181 = **928** |
| 64 | 231 | −23806 → −23575 = **231** |
| 96 | 57 | −23980 → −23923 = **57** |

Time 0 is 65535 (instant) and 127 is 1 mst/tick — two minutes per octave. The initial offset also
checks out: gliding from key 48 into a note whose base pitch is 72037 gives −24037, and the first
observable tick already shows one step of decay applied.

**Which source key, and when it arms.** Two paths, and they differ:

- **CC84** supplies the key outright (`voice+0x162`), the glide departs from a flat
  `key × 1000`, and it fires **even in poly with other notes ringing** — measured. The engine
  consumes the byte at note-on and resets it to `0xff`, so it glides exactly one note.
- **CC65** is the sustained mode, and it only arms when the part has **no live note groups**
  (`part+0x270 == 0`). Measured: the same two notes struck over a still-ringing first note produce
  **no glide at all**, while in mono mode they glide. Mono short-circuits the test rather than
  re-reading it after the voice flush, so a part that has just chased its own voices away still
  counts as quiet.

**Note `voice+0x6c` does not include the glide** — that field is written before the glide is folded
in, so tracing it shows a portamento note apparently starting in tune. The glide reaches the phase
increment (`voice+0xb8`) via the working accumulator; `voice+0x8c` is the offset itself. Two hours
of "portamento is not implemented in SC-VA" came from reading the wrong field.

## The "random detune" is random *pan* — and `g_pitch_split_*` is the pan law `[confirmed]`

An earlier pass recorded `voice+0xf4/0xf6` as a randomised **pitch** pair fed from
`g_pitch_split_coarse/fine`, consumer unknown. Following the consumer settles it: they are the
voice's **output-bus sends**, and the tables are the **pan law**. Both names were wrong.

**The consumer.** `voice_block_process` copies four voice words — `+0xf4`, `+0xf6`, `+0xf0`,
`+0xf2` — into four per-voice arrays, adding the voice's base bus number (`voice+0x06`) to the
first and that number **+1** to the second. `voice_compute_mod_rates` then splits each word:

```
level = (word >> 6) / 1024.0     (floats at 0x181a1d930/1da30/1db30/1dc30)
bus   =  word & 0x3f             (ints   at 0x181a6e4b0/6e5b0/6e6b0/6e7b0)
```

and `voice_output_accumulate` mixes the voice's block into `g_output_bus_accum[bus]` scaled by
`level`. So each voice has **four (bus, level) sends**, and the `+0`/`+1` pair on a shared base is
the dry stereo pair — left and right are adjacent buses, which is why the two words are written
together and why one table read forwards and the other backwards.

**The tables are one pan curve read from both ends.** `g_pitch_split_coarse` (`0x1819a2fa0`) and
`g_pitch_split_fine` (`0x1819a3020`) are 0x80 apart, and the code indexes the second
*negatively* — `fine[−i]` — so both views walk the same 128 bytes in opposite directions:

| position | 0 | 32 | 64 | 96 | 127 |
|---|---|---|---|---|---|
| forward (right) | 0 | 35 | 75 | 109 | 127 |
| backward (left) | 127 | 109 | 75 | 35 | 0 |

Symmetric, 75/75 at centre. Rename to `g_pan_curve` (`fine` is simply its far end).

**The randomisation is GS RND pan, and it is narrower than it looks.** `tvf_env_prep` resolves the
position as `part+0x3dd + (partial pan − 0x40)` (plus the drum key's own pan), clamped to 0..0x7f
— and takes the random branch when the pan byte is **zero**, drawing `prng_lfsr() >> 9` for a fresh
position per note. Two sources can be zero, and one famously cannot:

- **Drum key pan zero → random.** The NRPN `1Ch` path stores the value verbatim, so a kit key can
  hold zero. Confirmed: the plane reads 0 after the NRPN, and `tvf_env_prep` branches on it.
- **Part panpot zero → random**, but **CC#10 cannot produce it**: the CC10 handler
  (`0x180065f90`) is `value == 0 ? 1 : value`, so the wheel's zero is stored as one. Measured —
  CC#10 = 0 gives position 1 (hard left, `L=0x7f R=0x00`) on every strike, not a new position each
  time. Only the GS SysEx panpot writes a true zero. That matches the GS spec, where the panpot's
  RND value is reachable by SysEx and CC#10 is a plain 1..127 control.

The random source is `prng_lfsr` — the same generator as the pitch start jitter and the random LFO
waveforms, already documented with its `0xEFA6`/`0x9C23` reset seeds.
