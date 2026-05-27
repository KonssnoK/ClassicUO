# AMOU animation frame format — VERIFIED ✅

`build/animationframe/{body:06}/{action:02}.bin` entries in
`AnimationFrame{1..6}.uop`. Magic = `AMOU` (actually only the first 3 bytes
`'A','M','O'` are checked — the 4th byte is read but ignored).

**Source of truth:** ported from UOReader 0.8.7 by Kons (decompiled from
the public Google Code Archive release). UOReader was the original tool
that decoded these frames and was itself a port of `KRFrameViewer`. The
algorithm here is byte-for-byte equivalent to UOReader's
`UOFrameBin.cs` + `UOFrame.cs`.

A working Python reference decoder is in
[`tools/ec_research/scripts/amou_decode_verified.py`](../scripts/amou_decode_verified.py).
It produces correct sprites for human-male idle (body 400, action 0).

## File layout

```
0x00..0x2B   header (44 B)
varies       palette       at header.colour_offset, colour_count × 4 B
varies       frame table   at header.frame_offset, frame_count × 16 B
varies..eof  pixel stream  (one slice per frame, indexed by frame.data_offset)
```

## Header (44 B)

| Off  | Type   | Field           | Notes                                          |
|------|--------|-----------------|------------------------------------------------|
| 0x00 | char[4]| magic           | `'AMOU'` — only first 3 bytes checked          |
| 0x04 | u32    | version         | 1 in all samples                               |
| 0x08 | u32    | total_size      | end of file / end of pixel stream              |
| 0x0C | u32    | body_id         |                                                |
| 0x10 | i16    | init_x          | global bbox top-left X                         |
| 0x12 | i16    | init_y          | global bbox top-left Y                         |
| 0x14 | i16    | end_x           | global bbox bottom-right X (exclusive)         |
| 0x16 | i16    | end_y           | global bbox bottom-right Y (exclusive)         |
| 0x18 | u32    | colour_count    | palette length in entries                      |
| 0x1C | u32    | colour_offset   | absolute file offset to palette                |
| 0x20 | u32    | frame_count     | REAL frame count                               |
| 0x24 | u32    | frame_offset    | absolute file offset to frame table            |

Bbox convention: width = `end_x - init_x`, height = `end_y - init_y`
(no `+1` — `end` is exclusive).

## Palette

Each entry = `(R, G, B, alpha_flag)`. 4th byte is body-specific marker
(special-color tag), not an alpha channel — render with `A = 255`.

## Frame table

`frame_count` × 16 B at `frame_offset`:

| Off   | Type   | Field           |
|-------|--------|-----------------|
| +0x00 | u16    | id              |
| +0x02 | u16    | frame_index     |
| +0x04 | i16    | init_x          |
| +0x06 | i16    | init_y          |
| +0x08 | i16    | end_x           |
| +0x0A | i16    | end_y           |
| +0x0C | u32    | data_offset_rel | offset to pixel data, RELATIVE to this frame entry's start |

Pixel data for frame *i* begins at `frame_offset + i*16 + data_offset_rel`.
Pixel area starts at `frame_offset + frame_count * 16`.

## Pixel stream — anti-aliased RLE

Decode row-major across the frame's bbox. Each iteration reads opcode `b`:

```
b = stream[off++]
if b < 128:
    # transparent skip
    advance cursor by b pixels
else:
    n_solid = b - 128
    b2 = stream[off++]
    hi = b2 >> 4         # leading-edge AA weight (0..15)
    lo = b2 & 0x0F       # trailing-edge AA weight (0..15)

    if hi > 0:
        idx = stream[off++]
        # 1 anti-aliased pixel: blend palette[idx] with whatever is at
        # the cursor (i.e. background or a previously-written pixel) at
        # weight hi/16.
        write blend(palette[idx], prior, hi)
        advance 1 px

    for _ in range(n_solid):
        idx = stream[off++]
        write palette[idx]    # solid (alpha=255)
        advance 1 px

    if lo > 0:
        idx = stream[off++]
        write blend(palette[idx], prior, lo)
        advance 1 px
```

Blend math (UOReader's nibble blender, reduces to a 4-bit weighted lerp
per channel):

```
out_r = (idx_r * w + prior_r * (16 - w)) / 16
out_g = (idx_g * w + prior_g * (16 - w)) / 16
out_b = (idx_b * w + prior_b * (16 - w)) / 16
out_a = 255
```

Cursor advances left-to-right, wrapping to the next row at `width`.
Stream is consumed until `y >= height`.

## What was wrong in the prior research doc

Before recovering UOReader, the speculative layout had:

- "atlas_w / atlas_h" at 0x18 / 0x1C — these are actually
  `colour_count` (u32) and `colour_offset` (u32).
- "nominal_count" at 0x20 — actually the real `frame_count`. (For
  body 400 action 0: 50 entries, all valid.)
- "palette_byte_size" at 0x24 — actually `frame_offset`. The palette
  size = `colour_count * 4`.
- "8 bytes flags" at 0x28..0x2F — actually the last 4 bytes of that
  range (`frame_offset`) are part of the header; the prior bytes are
  `frame_count`.
- "frame table entry's u32 is cumulative end-offset" — actually it's a
  per-entry data offset relative to that entry's own start.
- "bbox uses inclusive max" — actually `end_x`/`end_y` are exclusive.
- Pixel stream was completely undecoded; format is anti-aliased RLE
  (this section above).

## Test fixtures

- Body 400 (human male), action 0 (idle) — `colour_count=256`,
  `frame_count=50`, total ~64 KB. Decoded sprites land at
  `dump_amou_decoded/frame_00_idx001.png` etc. — ~51% pixel coverage
  matches a humanoid figure.
- Located in `AnimationFrame1.uop` at
  `build/animationframe/000400/00.bin`.

## UOReader provenance

UOReader 0.8.7 (2013) by Kons — released on the Mythic-era Ultima Online
fan community. Source is on the Google Code Archive:
`https://storage.googleapis.com/google-code-archive-downloads/v2/code.google.com/kprojects/UOReader_0.8.7.zip`.
The animation viewer there is described as ported from `KRFrameViewer`,
made by Kons and Wim during the Kingdom Reborn era — so this format has
been continuous from KR through EC.
