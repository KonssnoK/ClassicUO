# AMOU animation frame format — research-in-progress

The `build/animationframe/{body:06}/{action:02}.bin` entries in
`AnimationFrame{1..6}.uop` are binary blobs starting with the magic
`AMOU`. This doc captures the format as decoded so far.

## What's verified ✅

### Outer structure
```
0x00..0x1F   header (32 B)
0x20..0x23   u32 nominal_count       (NOT real frame count — see below)
0x24..0x27   u32 palette_byte_size
0x28..0x2F   8 bytes flags / per-body data
0x30..pe     palette: num_colors × (u8 R, u8 G, u8 B, u8 alpha-flag)
pe..pe+N16   frame index table: N entries × 16 B
pe+N16..end  pixel-data stream
```

### Header (32 B)
| Offset | Type | Field |
|---|---|---|
| 0x00 | char[4] | magic = `"AMOU"` |
| 0x04 | u32 | version (= 1 in all samples) |
| 0x08 | u32 | dsize (= file size sanity) |
| 0x0C | u32 | body_id |
| 0x10 | i16 × 4 | global bbox (minX, minY, maxX, maxY) |
| 0x18 | u16 | atlas_w (= 256 in samples) |
| 0x1A | u16 | padding |
| 0x1C | u32 | atlas_h (= 40 in samples) |

### Sub-header (0x20..0x2F)
| Offset | Type | Field |
|---|---|---|
| 0x20 | u32 | nominal_count — semantics unclear; for human-male idle = 50 but only 47 real frame entries exist (idx 4..50). Possibly `last_idx + 1` or total frames across this body's anim set, not a per-file count |
| 0x24 | u32 | palette_byte_size — pal_entries = pal_byte_size / 4 |
| 0x28..0x2F | bytes | 8 bytes, body-specific. First 4 bytes vary per body (`03 02 03 00` for human male, `11 0a 07 00` for ogre, `1d 0a 13 00` for human male attack). Suspected: per-direction frame-count tuple |

### Palette (variable size)
- Each entry = 4 B: `R, G, B, alpha_flag`
- `alpha_flag` is usually 0 or 1 (rarely 255). Likely a "is special color" marker (shadow / transparent / glow)
- Human male body has 266 palette entries
- Distribution of `alpha_flag` in sample: 67% = 0, 31% = 1, 2% = 255

Visual confirmation: rendering the palette as a strip shows skin tones,
greys, and a few highlight colors at the end (blue/red/yellow) — exactly
what you'd expect for a character. The palette IS correct.

### Frame index table (16 B per entry, variable count)
| Offset | Type | Field |
|---|---|---|
| +0x00 | u16 | padding (always 0) |
| +0x02 | u16 | frame_index (sequential, but starts at non-zero — e.g. 4 for human idle) |
| +0x04 | i16 × 4 | per-frame bbox (relative to character origin) |
| +0x0C | u32 | cumulative end-offset of THIS frame's pixel data within the pixel-data stream |

Number of entries determined empirically (parse until either pad ≠ 0,
bbox values outside global bbox, or end_offset doesn't monotonically
increase). For human male idle: 47 entries with idx 4..50.

Per-frame size = `entry.end_off - prev_entry.end_off` (or
`entry[0].end_off` for the first frame). Frame 0 is usually larger than
the others (e.g. 4877 B vs ~1300 for frames 1..N) — its tail probably
contains direction-tables or per-anim metadata; treat with care.

## What's NOT verified yet ❌

### Pixel stream encoding

For human male idle, frame 1 (idx 5) has bbox 31×69 = 2139 pixels but
only 1309 bytes of pixel data (ratio 0.61). This means the stream is
**compressed**, not raw indexed bytes.

Tried and rejected:
1. **CC anim.mul u32 packed RLE** (`(x << 22) | (y << 12) | count`, then
   `count` palette indices, terminator `0x7FFF7FFF`) — produces an
   empty image. The packing must be different.
2. **u16 with high bit = command** — too simple to fit the observed
   ratios across frames.

Hex dump of frame 0 (4877 B) first 80 bytes:
```
0c80 2824 1680 9441 2619 846c d8 65 65 65 65
d6 1886 0626 65 65 65 65 11 fd16 86 2a e3 41 65 65 65 65
0fbd 1686 4d82 d816 6526 ccfd 1687 a1f1 d624
d8c5 a4fd e84e 1586 4ce7 823a d6a4 e7bd cb13 8027 2665 80df
```

Patterns observed:
- Many `0x65` runs in frame 0 (= palette index for the most common color)
- High-bit-set bytes (`0x80`, `0x86`, `0xCC`, etc.) appearing in regular
  positions — likely command/escape opcodes
- `0xff` runs in some frames — could be transparent-skip markers
- Frame 0 has anomalous ratio (2.29×), suggesting per-direction header
  or sub-table embedded BEFORE the pixel run stream

## Suggested next research directions

1. **Disassemble the Mythic binary loader** for AMOU specifically. The
   factory is `AVUOAnimationFrameSetFactory` (per existing
   `AnimationFrame.md`). **Attempted** with our 47k-function Ghidra
   dump and found no hits:
   - The literal `'AMOU'` is NOT a string in any function body — the
     magic check must be `memcmp(buf, "AMOU", 4)` against a `.rdata`
     constant whose address Ghidra reads as raw u32 but doesn't render
     as a recognizable string in the decompile.
   - Searching for the u32 constant `0x554F4D41` (`'AMOU'` LE) in any
     decompiled body — also no hits.
   - String matches for `AVUOAnimation*` class names — none in our
     dump.
   - Searching for typical RLE-decoder signatures (nested loops, byte
     masks, palette indexing) yielded 320 candidates, all of which are
     CRT/Gamebryo math/lib functions when inspected.
   - The Mythic asset registry uses a TYPE TAG (we saw `0x6000000` for
     TileArt) dispatched through `FUN_00a72320`'s table. AMOU likely
     has its own type tag; finding it would reach the right factory.
     Try `0x7000000`, `0x8000000` etc. as potential AnimationFrame
     resource type tags and grep for their use.
2. **Differential byte analysis** across known-similar frames. The
   first frames of an idle animation should be highly redundant in the
   pixel area too. Compare frames 1-3 byte-by-byte to find structural
   markers.
3. **Render frame 0 byte-by-byte raw** (treat each byte as a palette
   index and lay it out row-by-row at bbox dimensions) — if any
   recognizable shape appears, even garbled, that confirms 1 BPP
   indexed encoding with a different stream layout.
4. **Look at the legacy KR anim format documentation** — some Mythic
   internal docs or fan-decoded specs exist for the Kingdom Reborn
   client which used a near-identical animation system.

## Test fixtures

Working sample for debugging:
- Body 400 (human male), action 0 (idle) — small, 50 frame indices
  (47 valid), 266-entry palette, 64 KB total
- Located at `AnimationFrame1.uop : build/animationframe/000400/00.bin`

A Python parser is in `tools/ec_research/scripts/98_amou_decode.py`
(structure parse) and `99_amou_render.py` (attempted RLE decode).
Palette renders correctly to `dump_amou/palette.png`; frames are still
blank pending pixel-stream decode.
