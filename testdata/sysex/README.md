# `sysex/` — messages the corpus never sends

## `tonemap_sysex_map*.mid` — selecting the vintage by SysEx

Four files, one per map, each sending `40 4x 01 <map>` to all sixteen blocks after a GS reset and
then playing a three-note chord on `Strings`.

They exist because **nothing else here selects a map that way**. Every fixture in both repositories
gets its map from a command-line argument, so the SysEx route had no coverage at all — and this
engine dropped the message entirely until it was measured.

The map is `40 4x 01`, writing `part+0x44e` clamped to 1..4. Its neighbour `40 4x 00` writes
`part+0x44d`, the tone-space selector, and does **nothing** to the map. Sweeping every address in
the `40 1x` and `40 4x` blocks against a part dump moves exactly those two bytes and nothing else;
see FINDINGS, and reproduce with `scdec mapsysex sweep <value>`.

Render one through the harness with a *different* map argument to see the SysEx win:

    scdec <dll> smf testdata/sysex/tonemap_sysex_map1.mid out.wav 4 1.0

The result is byte-identical to the same chord rendered natively at map 1.

**The vintage is a default, not a ceiling.** The SysEx and CC#32 write the same byte and the last
writer wins, so a file that sends its own bank LSB after one of these overrides it.
