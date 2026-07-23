# SCCore.dll data layout

Everything the Sound Canvas VA engine needs — the wave ROM, every synth curve/key-follow table, and
the patch directory — lives **inside `SCCore.dll`**. This document + [`tables/manifest.json`](../tables/manifest.json)
are the map, so a downstream implementation can read the static data straight from the DLL and never
re-reverse an offset.

The Python engine in this repo is a **reference model / proof of concept**. The intended product is a
cross-platform engine that loads this DLL, reads the data below, and plays MIDI from an SMF file or a
live keyboard. The `tables/*.bin` files are a convenience cache — each is a byte-for-byte slice of the
DLL at the offsets listed in the manifest, shipped only so the repo need not redistribute the
copyrighted DLL.

## Pin the DLL version first

The tables are version-specific. This work was done against exactly this file:

| field | value |
|---|---|
| filename | `SCCore.dll` |
| size | **27,347,456** bytes |
| SHA-256 | `117E6AA147A96FBDE5E10D2CAF16C89965ACC1E44235FD245992216CC620BDB1` |
| SHA-1 | `CF9DCE5A0CABEE06792E884673B8BEEF806F1AED` |
| MD5 | `DBD9A30C168EFEF577D40A28D9ADF37D` |
| PE timestamp | `1572416468` = 2019-10-30 06:21:08 UTC |
| file mtime | 2020-01-19 UTC |
| product | Roland VS Sound Canvas VA |

The Win32 version resource is empty (no `FileVersion`), so identify the build by **hash + PE timestamp
+ size**. A different SC-VA build may move tables; re-run the generator (below) to re-derive offsets.

## Address model

The DLL's preferred image base is `0x180000000`. Symbol VAs (as seen in a disassembler) map to raw
**file** offsets by a per-section constant:

| region | mapping |
|---|---|
| `.rdata` curve / key-follow tables | `file_offset = VA − 0x180000000 − 0x1000` |
| resample-kernel section (`g_interp_coef_table`) | `file_offset = VA − 0x180000000 − 0x1400` |
| data section — wave ROM + patch directory | own base offsets, one per region (see manifest) |

The Python reference reads the raw file, so it uses file offsets directly (`numpy.frombuffer(DLL,
…, offset=…)`). If instead you read the **loaded/relocated** image (e.g. via `scdec dumpmem`), use
`loaded_base + (VA − 0x180000000)` — the virtual RVA, which differs from the file offset by the
section skew above.

## What's where

- **Wave ROM** — two banks, file offsets `0x92700` (A: stacked SC-88/88Pro) and `0x1092730` (B: 8820),
  ~12 MB each, addressed in 1 MB blocks. Each sample is a block-floating-point ADPCM pair of streams
  (per-sample delta + per-16-sample scale nibble); decode = `cumsum(delta << (scale + 10)) * CONST`.
  See `decode_wave` in `scvx_engine.py`.
- **Patch directory** (data section): `tone` (0x100-stride records = 0x24 header + four 0x6e partial
  blocks), `multisample` (0x8c stride, key/vel zone → wave#), `wavedesc` (ROM coords + root key + loop),
  plus the `layered` alt-articulation table and three lookup LUTs. Drum kits: `0x50C`-stride records at
  VA `0x18AD950`, selected through a bank-row + program-map pair of LUTs.
- **Synth curves & key-follow tables** (`.rdata`): the TVA/TVF/pitch envelope curves, the shared
  segment-rate machine (`g_rate_curve`, `g_env_rate_out`, `g_env_scale_curve`, `g_env_shape`), the LFO
  tables, the 4-tap resample kernel, and the pan table. Full list with offsets, sizes, dtypes, and
  purpose in [`tables/manifest.json`](../tables/manifest.json) → `cached_tables`.

## Regenerating the manifest / extracting tables

```
python tools/gen_manifest.py "C:/Program Files/Roland VS/SOUND Canvas VA/SCCore.dll"
```

It locates every `tables/*.bin` in the DLL by byte-exact content match, records the offset/size/dtype/
VA, and re-emits `tables/manifest.json` (including fresh DLL hashes). To pull a table straight from the
DLL, slice `size` bytes at `file_offset`. From the loaded image instead, `scdec dumpmem <VAhex> <count>
<out.bin>` reads it at the runtime VA.

All 48 cached tables match the DLL byte-for-byte at these offsets (one, `kf_tvfenv`, is an over-read
whose used rows 0–15 match; the unused high rows differ and are never indexed).
