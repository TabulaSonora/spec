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

| Window | Reference passage | A good result |
| --- | --- | --- |
| 4 ms | 0.72 | ≥ 0.72 |
| 20 ms | — | ≥ 0.88 |
| 250 ms | 0.91 | ≥ 0.91 |
| 1 s | — | ≥ 0.93 |
| level | within 0.5 dB | within 0.5 dB |

The reference column is the passage the verification record cites as correct despite looking poor —
a harpsichord section riding the sustain pedal, which is about as unfavourable as this comparison
gets.

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
and the level notice anything, and they notice it faintly. **A file whose point is pitch has to be
judged some other way** — by the pitch track, or by spectrum, or by ear.

Level and envelope agreement say nothing about spectrum either. Two renders can track each other's
loudness perfectly with one of them dull — the filter-envelope velocity response was found exactly
that way, sitting a third of an octave too open for a whole note while measuring +3.5 dB at 4–8 kHz.
A per-band comparison is the companion measurement and is not described here.

Nor does any of this establish bit-exactness. Where two implementations are *supposed* to be
identical — the static tables, the sample codec, the pitch and LFO tick streams — compare them
directly and demand equality. This method is for the rendered output, where the hardware itself is
not deterministic in the small.

## A note on where the number comes from

A bad-looking figure is a lead, not a verdict, and a good-looking one taken at a single window is
not evidence. Both directions of that have caught people out in this project: a set of effect
comparisons once passed while testing nothing at all, because the fixture windows were shorter than
the delays and both sides were silent and agreed perfectly. Assert that the signal is present before
asserting that it matches.
