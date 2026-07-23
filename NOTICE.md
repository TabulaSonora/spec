# Notice on third-party rights

## What this project licenses

The BSD 3-Clause licence in `LICENSE` covers **this repository's own contents**: the documentation in
`docs/`, the Python reference implementation (`scvx_*.py`), the tooling in `tools/`, and
`tables/manifest.json`.

All of it is original work — prose written from analysis, code written to describe what that analysis
found, and an offset map. It is licensed permissively so that an implementation in any language can be
built from it, including under the GPL.

That licence does **not**, and cannot, grant you any right in Roland's software or data.

## What is not here, and why

`SCCore.dll` and everything derived byte-for-byte from it remain Roland Corporation's:

- the 24 MB wave ROM embedded in the DLL, which is the literal Sound Canvas hardware mask ROM
- the extracted `tables/*.bin` slices
- the effect coefficient dumps in `tables/*.txt`
- the Ghidra decompile, `SCCore.decompiled.c`
- rendered or decoded audio

None of that is committed here. `.gitignore` excludes each category and `README.md` gives the command
that regenerates it from your own legally obtained copy of the DLL.

`tables/manifest.json` **is** tracked, deliberately: it records *where* each table lives inside the
DLL — offsets, sizes, and the project's own symbol labels. It is a map, not the territory.

## Naming

The symbol names throughout this project (`render_block`, `g_interp_coef_table`, `sampler_pcm`, and
the rest) are labels invented during reverse engineering to describe observed behaviour. They are not
Roland's names, and they are not authoritative. The `TG_*` exports are the sole exception: those are
genuine PE export symbols.

## Purpose

This is a preservation and interoperability effort on a discontinued product. Sound Canvas VA was
withdrawn from sale in September 2024. The work exists so that music written for the Sound Canvas can
still be played after the software that played it stops being available.
