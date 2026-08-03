# Comparing a render against the DLL

How to tell whether a reimplementation's audio matches `SCCore.dll`'s, and — more importantly — how
not to. This is a method, not a finding: nothing here is reverse-engineered, it is the measurement
the rest of the record is judged by.

## The short version

Compare **windowed RMS envelopes**, at several window sizes, plus level. Do not compare samples.

```
envelope(x, w)[i] = sqrt( mean( x[i*w : (i+1)*w]^2 ) )
```

Correlate the two envelope sequences with an ordinary Pearson coefficient, and report the level
difference in dB as `20 * log10(rms_b / rms_a)`. Mix to mono first; a stereo difference is a separate
question and mixing keeps a pan disagreement from masquerading as a timbre one.

## Why samples are the wrong unit

Two renders can agree on every note, every envelope and every filter setting and still correlate at
**near zero** sample by sample. In a dense passage the few-millisecond waveform is dominated by
beating between simultaneous notes, and beating is chaotically sensitive to differences far below
audibility — a one-count difference in a pitch word moves the interference pattern completely while
moving nothing anyone can hear.

The symptom is diagnostic once recognised: **sparse passages correlate far better than dense ones**
in the same file, because sparse passages have fewer voices to beat against each other. Reading that
as "the dense parts are broken" is backwards. It is the metric failing, not the engine.

A concrete case: `canyon.mid` rendered by the DLL against a reimplementation correlates at **0.047**
across the whole file, while its envelopes correlate at 0.775–0.938 and its level sits within
0.30 dB. The sample figure is not a weak pass, it is meaningless.

## What good looks like

Measured figures for a comparison judged correct, and the pass this record uses:

| Window | A good result |
| --- | --- |
| 4 ms | *advisory — see below* |
| 20 ms | ≥ 0.88 |
| 50 ms | ≥ 0.90 |
| 250 ms | ≥ 0.91 |
| 1 s | ≥ 0.93 |
| level | within 0.5 dB |

**The 4 ms window does not carry a threshold, and an earlier version of this document was wrong to
give it one.** A floor of 0.72 was taken from a single passage the verification record cites as
correct, and it does not survive five files:

| File | 4 ms | 20 ms | 50 ms | 250 ms | 1 s | level |
| --- | --- | --- | --- | --- | --- | --- |
| canyon (sparse) | 0.775 | 0.900 | 0.914 | 0.919 | 0.938 | −0.30 dB |
| sc50nn | 0.664 | 0.903 | 0.938 | 0.945 | 0.951 | −0.44 dB |
| transcendental (dense) | 0.631 | 0.935 | 0.980 | 0.995 | 0.998 | +0.03 dB |
| bad_apple (dense) | 0.573 | 0.811 | 0.880 | 0.935 | 0.964 | −0.09 dB |

Only the sparsest file clears 0.72 at 4 ms, and the two densest sit near 0.6 while agreeing at
0.98–0.998 by a quarter-second and within 0.1 dB on level. That is the beating sensitivity this
document opens by explaining, applied to its own threshold: **4 ms correlation is a function of how
dense the passage is, so a fixed floor measures the file rather than the engine.** Read it as a
number that should be *higher on sparse material than dense*, and be suspicious when it is not.

Note also that both dense files above exhaust a 64-voice pool. Where an engine steals voices its
stealing *policy* shapes the fine structure directly — different notes cut at different moments —
so on such files the 4 ms figure is partly measuring an allocator, not a synthesiser.

**The rise with window size is itself the signal.** A comparison that is genuinely right climbs
steeply from 4 ms to 250 ms and then flattens, because the disagreement is concentrated in the
fine structure that beating governs. A comparison that is *wrong* — a mistuned parameter, a missing
effect, a wrong envelope — stays flat or rises barely, because its disagreement is at the scale of
notes rather than of cycles. Report the whole curve, never one window.

## Before measuring anything

Three ways to get a meaningless number from a correct engine:

1. **Feed the DLL on its own block.** It renders in 320-sample blocks — 10 ms at 32 kHz, its 100 Hz
   control tick — and asked for any other count it chunks internally, taking pending events only at
   the start of each of its own blocks. A finer feed does not place events more precisely; it
   places them at the same moments while making the harness believe otherwise.
2. **Fix the gain.** An oracle normalised to its own peak cannot be compared across files, or
   against anything else. Write a fixed scale.
3. **Check for a constant lag before concluding anything.** Sweep a few thousand samples of offset
   over a window and take the best. A pure time offset destroys correlation at every window size at
   once, which is distinguishable from a real difference — and if the best lag *grows* through the
   file, the fault is a tempo-map bug in the harness, not the engine.

## What this does not measure

**Pitch.** This is the sharpest limit and it is easy to forget, because the metric looks so healthy
while missing it. An RMS envelope is a measure of *amplitude over time*: play the right note at the
right moment with the wrong pitch and the envelope barely moves. Measured case — `test_poly_bend.mid`
against an engine that does not implement polyphonic aftertouch at all, so the DLL bends the note a
whole octave and the candidate does not:

| Window | 4 ms | 20 ms | 250 ms | 1 s |
| --- | --- | --- | --- | --- |
| r | 0.575 | 0.921 | 0.990 | **0.998** |

A twelve-semitone error, and the one-second envelope calls it a 0.998 match. Only the 4 ms window
and the level notice anything, and they notice it faintly. **The octave-band spectrum is what
catches it**: the same pair measures −16.9 dB at 500 Hz and −10.4 dB at 2 kHz, because a note bent
to the wrong octave puts its energy in the wrong bands. This is why the tool gates on both.

Level and envelope agreement say nothing about spectrum either. Two renders can track each other's
loudness perfectly with one of them dull — the filter-envelope velocity response was found exactly
that way, sitting a third of an octave too open for a whole note while measuring +3.5 dB at 4–8 kHz.
`tools/compare_envelope.py` therefore also gates on a Welch-averaged **octave-band spectrum**
(±2 dB per band above a −45 dB floor, shift-invariant so phase and delay cannot bias it) and a
**delay-corrected envelope PSNR** at 250 ms (≥ 20 dB, after a bounded ±32 ms alignment search that
absorbs fixed offsets like the DLL's ~5 ms event-queue latency). Thresholds are calibrated on the
corpus — one known-good pair and two known-bad — and recorded in the tool beside their provenance;
they are expected to move as the corpus grows.

Nor does any of this establish bit-exactness. Where two implementations are *supposed* to be
identical — the static tables, the sample codec, the pitch and LFO tick streams — compare them
directly and demand equality. This method is for the rendered output, where the hardware itself is
not deterministic in the small.

## The shape of the curve tells you which kind of wrong

Two failures from the same corpus, and they do not mean the same thing:

| File | 4 ms | 20 ms | 50 ms | 250 ms | 1 s | level |
| --- | --- | --- | --- | --- | --- | --- |
| bad_apple | 0.573 | 0.811 | 0.880 | 0.935 | 0.964 | −0.09 dB |
| Right In The Night | 0.648 | 0.806 | 0.816 | 0.812 | 0.847 | **−2.50 dB** |

The first **rises** — 0.57 to 0.96 — and its level is right. That is fine structure disagreeing while
the music agrees, the beating effect this document opens with, and it points at something small.

The second is **flat**: 0.81 at 20 ms and 0.81 at a quarter-second, with the level 2.5 dB out. A
disagreement that does not shrink as the window widens is not beating; it is at the scale of notes.
Something is wrong with the sound itself.

Looking at what that file asks for turned up a real omission — it sets `40 01 33` and `40 01 3A`,
the individual **reverb level** and **chorus level**, which the engine implemented only as macros
and dropped as single edits. That looked like the whole answer.

**It was not.** Implementing them moved the level from −2.50 dB to −2.29 and the correlations by
about 0.005. The file sets each of those addresses exactly *once*, so a single edit was always going
to be worth a fraction of a dB, and the shape of the failure had been read as confirmation of the
first plausible cause found.

The lesson is the useful part. A flat curve does say the disagreement is at the scale of notes
rather than of cycles, and that is worth acting on — but it does not say *which* thing, and a
missing feature that the file genuinely uses is not thereby the cause. Measure the fix; a hypothesis
that survives only because it sounded right is not evidence. A second hypothesis — that the file's heavy NRPN use was uncovered — also failed, and failed
usefully. Of its 289 NRPN edits, 285 are parameters the engine does implement (`01 20` TVF cutoff
×268, `01 21` resonance, `01 63`/`64`/`66` envelope) and the remaining handful are drum overrides it
also implements. Coverage was not the gap.

What the count exposed instead is *when* they are read. The engine latches those parameters at
**note-on**, while the module applies them continuously: a filter swept 268 times across five
minutes moves the sound of notes that are already sounding, and an engine that samples the value
once per note simply does not follow. That is a difference at the scale of notes, sustained for the
whole file — the shape the flat curve was pointing at all along, arrived at by counting rather than
by guessing which feature was missing.

That was wrong too — making the parameter continuous moved the file by 0.02 dB. Four hypotheses,
four real differences correctly fixed or excluded, none of them the one that mattered.

**Rendering each channel on its own found it in one step.** Split the file per channel, render both
engines on each part, and compare:

| Channel | Notes | Program | 250 ms | Level |
| --- | --- | --- | --- | --- |
| 9 (drums) | 6053 | — | 0.996 | −0.25 dB |
| 12 | 1696 | 28 | 0.967 | −1.12 dB |
| 7 | 1357 | 38 | 0.988 | **−4.43 dB** |

The busiest channel by far is fine. Two melodic channels carry the whole deficit, and — this is the
informative part — their **correlations are high**. 0.988 at a quarter-second means the notes, the
timing and the envelope shapes are right; only the absolute level is wrong. A wrong *instrument*
would have hurt correlation as well, so this is not patch resolution picking the wrong sound. It is
the right sound at the wrong gain.

Narrowing further put it on one part and one number. Channel 7's deficit is **constant at −4.4 dB
from the moment it enters** — independent of what it plays, so a static gain rather than anything
musical — while every one of its 1357 notes sounds and none is stolen. A *single* note of the same
program at the same volume matches the DLL to +0.33 dB, so it is not the patch's level either.

What differs is the stereo placement, and with it the total energy:

| | L | R | balance | total |
| --- | --- | --- | --- | --- |
| DLL | 752.8 | 941.3 | −1.94 dB | 1205 |
| this engine | 341.4 | 591.8 | −4.78 dB | 683 |

The part is panned right in both, further right here, and **4.9 dB quieter in total power** — not
merely quieter in the mono sum, so it is not an artefact of mixing down. A pan that loses energy as
it moves off centre is a pan *law* symptom, and this engine's law was verified against a controller
sweep to 3e-05 — which means the law is likely right and the value reaching it is not.

Probing that one part with two-second files narrowed it further, and produced a result worth
keeping whatever the cause turns out to be:

| Probe (program 38, one note) | Total against the DLL |
| --- | --- |
| pan 64, no sends | −0.16 dB |
| pan 94, no sends | +0.35 dB |
| pan 64, chorus send 127 | −0.80 dB |
| pan 64, reverb send 127 | −0.22 dB |
| pan 94, chorus 127, reverb 40 | **−2.81 dB** |

Read down that table rather than at any one row. The dry path is right, at either pan. The **reverb**
send is right — its deficit never rises above the dry baseline at any send level, and its tail is a
constant +1 dB. The **chorus** send is not: the deficit grows with the send, from −0.16 at zero to
−0.80 at full.

But chorus alone is 0.8 dB and the combination is 2.8. Pan is right on its own and sends are right
or nearly so on their own, and together they are three times worse than the sum. That is an
*interaction*, and it is the thing to isolate next — most likely how the send feed relates to pan,
since this engine feeds its sends from a pre-pan mono signal and a difference there would be
invisible until both are used at once.

A pan × send grid on the same probe separated them completely:

| Pan | send 0 | send 127 | difference the send makes |
| --- | --- | --- | --- |
| 0 (hard left) | −0.16 dB | +1.35 dB | **+1.51** |
| 32 | −0.16 dB | +0.42 dB | +0.58 |
| 64 (centre) | −0.16 dB | −1.20 dB | −1.03 |
| 94 | −0.16 dB | −2.29 dB | −2.12 |
| 127 (hard right) | −0.16 dB | −2.64 dB | −2.48 |

**The dry path is exactly right.** −0.16 dB at every pan, to two decimal places, five positions
across the range. Pan is not the problem and neither is the patch.

**The send path's error is a monotonic function of pan** — from +1.51 dB at hard left to −2.48 dB at
hard right, passing through zero somewhere left of centre. A send fed from a pre-pan mono signal
cannot do that: its wet output should be identical at every pan, leaving only the dry to move. So
either the send feed is not pan-independent, or the wet return is panned when it should not be.

The asymmetry is the sharpest clue. A correct implementation is symmetric about centre; being 4 dB
apart at the two extremes says something in the wet path treats left and right differently. Nothing
in a mono send should.

**Then subtracting the dry render from the wet one settled it, and overturned the paragraph above.**
The send does not touch the dry path, so `render(send 127) − render(send 0)` is the chorus return on
its own. Isolated that way:

| Pan | DLL wet L / R | this engine L / R | balance |
| --- | --- | --- | --- |
| 0 | 415.9 / 414.7 | 363.5 / 362.5 | +0.03 dB both |
| 64 | 415.7 / 415.0 | 363.5 / 362.6 | +0.01 dB both |
| 127 | 415.7 / 415.0 | 363.3 / 362.7 | +0.01 dB both |

The wet return is **perfectly pan-independent and perfectly centred in both engines**, to a tenth of
a decibel across the whole pan range. So the send is not pan-dependent, the wet is not panned, and
the conclusion drawn from the grid — that one of those must be true — was wrong. The grid measured
*total* energy of dry plus wet; a centred wet summing with a dry that moves produces a pan-dependent
total on its own, with nothing pan-dependent in the send at all.

What the isolated wet does show is exact: it is **1.17 dB low, identically at every pan and in both
channels**, and its waveform is *anti*-correlated at zero lag (−0.54) while correlating at **+0.74
when shifted 424 samples**. A later re-examination against the live engine dumps (see FINDINGS)
demoted the lag reading: the chorus tap sweeps ±50 samples on a 2.73 s LFO whose phase free-runs
from engine start, so two engines' returns compare at different points of the sweep and the
correlation peak measures their *phase difference*, not the base delay. And a phase-matched sweep
then dissolved the level reading too: at the matched phase the wet agrees to 0.04 dB, so the
"1.17 dB deficit" was the phase offset as well. A free-running modulated delay makes even the
*level* of a windowed wet measurement phase-dependent — a constant per-file bias of up to ±1.5 dB
of the wet that no averaging removes. Chorus-heavy material cannot be level-compared across engines
until the LFO phase is pinned.

The general point stands and gains a second half. A defect that only appears when two features
combine survives any amount of testing each alone — but a metric that sums two signals will also
manufacture apparent interactions between them. **Subtract to isolate before concluding.** One
subtraction answered what ten grid renders had only mislabelled.

Two lessons. **Localise before theorising**: one splitting script answered in one pass what four
feature hypotheses could not, because it asked where the difference is rather than what it might be.
And **read correlation and level together** — high correlation with a large level error is a
different fault from low correlation with a right level, and only the pair distinguishes them.

So read the curve before reading any single number. A rising curve with a right level is a lead
about detail; a flat curve with a wrong level is something at the scale of notes. Then go and find
it, and confirm by the number moving.

## A note on where the number comes from

A bad-looking figure is a lead, not a verdict, and a good-looking one taken at a single window is
not evidence. Both directions of that have caught people out in this project: a set of effect
comparisons once passed while testing nothing at all, because the fixture windows were shorter than
the delays and both sides were silent and agreed perfectly. Assert that the signal is present before
asserting that it matches.
