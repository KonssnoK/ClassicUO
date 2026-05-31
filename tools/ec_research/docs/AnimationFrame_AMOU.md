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

## Live-engine integration findings (CUO port)

Below are post-port discoveries from wiring the decoder into ClassicUO
(see `src/ClassicUO.Renderer/Animations/EcAnimation.cs` and
`Animation.cs::TryBuildEcFrames`).

### Direction stride: 5 dirs × N frames (NOT 10 × N)

Empirically verified by playback: AMOU stores all directions
concatenated, with `frames.Length / 5` frames per direction. We tested
10 dirs × `frames.Length/10` first — it produced wrong facings on
playback. So the layout matches CC's `MAX_DIRECTIONS = 5` convention
(dirs 0..4 stored, mirrored to 5..7 at draw time).

For body 400 idle (50 frames): 10 frames per direction.

### Per-frame anchor must reference MAIN bbox

The per-frame `(InitX, InitY)` bbox shifts between frames as the body
animates (limbs moving, etc.). Anchoring each frame by its own
`InitX` causes visible **vibration** — the figure's pivot point jumps
between frames.

Per UOReader's `AnimationFrames.cs:258-262`, each frame's content is
blitted into a body-wide canvas of `MainBbox` dimensions at offset
`(frame.InitX - main.InitX, frame.InitY - main.InitY)`. Every frame
shares the same canvas size, so the body anchor stays stable across
frames. Our CUO port builds canvas-sized pixel buffers and uses CC's
`(CenterX, CenterY)` convention computed from the **main bbox**:

```
CenterX = mainCanvasW / 2
CenterY = 0                          (body's natural floor sits at canvas bottom)
mainCanvasW = mainEndX - mainInitX
mainCanvasH = mainEndY - mainInitY
```

### Anti-aliased edges: encode as partial alpha

UOReader's pixel decoder blends AA edge pixels against the *background
color of the preview canvas* (PaleGreen) — a preview-rendering trick.
When we ported that to render against the game world, edges came out
muddy because we were blending against transparent black.

Correct port: at decode time, for AA edge pixels (1..15 weight), keep
the palette RGB intact and **encode the weight as partial alpha**
(`alpha = w * 255 / 16`). The GPU composites them against the live
scene at draw time. Solid run pixels keep `alpha = 255`.

### Color: use SHADER_PARTIAL_HUED, not SHADER_NONE

The AMOU palette stores **neutral / greyscale skin tones**. The
character's body hue is applied at draw time via CC's
partial-hue shader (pixels where `R == G == B` get tinted, others
pass through). Bypassing the hue (`SHADER_NONE`) leaves the figure
washed-out grey. `SHADER_PARTIAL_HUED` matches CC's behavior and
produces correctly-skinned characters.

### Action numbering: AMOU uses HighAnimationGroup universally

This is the big one for non-human bodies.

CC's `GetGroupForAnimation` returns action IDs in **per-body-type
enums**: `PeopleAnimationGroup` for humans, `LowAnimationGroup` for
animals, `HighAnimationGroup` for monsters. So CC's "idle action"
is `People.Stand=4` for a human, `Low.Stand=2` for a cow,
`High.Stand=1` for a monster.

But AMOU files are stored per-body keyed by `{action:02}.bin` and
the action numbers inside the file **always follow
HighAnimationGroup numbering** regardless of the body's CC type.
So body 216 (cow) action 2 in AMOU is **Die1** (High.Die1 = 2),
not **Stand** (Low.Stand = 2).

#### Symptom

Cow plays the death animation when CC asks for idle — CC sends
action 2, AMOU interprets it as Die1.

#### Fix

In `TryBuildEcFrames`, translate the CC action number from the body's
effective group into High before the AMOU file lookup. Translation
table for Low → High:

| CC `LowAnimationGroup` action | AMOU action (High) |
|---|---|
| 0  Walk    | 0  Walk          |
| 1  Run     | 0  Walk          |
| 2  Stand   | 1  Stand         |
| 3  Eat     | 7  Misc1         |
| 5  Attack1 | 4  Attack1       |
| 6  Attack2 | 5  Attack2       |
| 7  Attack3 | 6  Attack3       |
| 8  Die1    | 2  Die1          |
| 9  Fidget1 | 17 Fidget1       |
| 10 Fidget2 | 18 Fidget2       |
| 11 LieDown | 14 Misc4         |
| 12 Die2    | 3  Die2          |

Verified visually for cow (body 216): AMOU `01.bin` = standing cow,
`02.bin` = collapsing cow (death). My remap of CC's Low.Stand(2) →
AMOU action 1 lands on idle, fixing the death-when-standing bug.

#### Don't over-remap: respect the `CalculateOffsetLowGroupExtended` flag

Some `Animal`-typed bodies have the `CalculateOffsetLowGroupExtended`
flag (0x20 in `mobtypes.txt`). With that flag, CC switches the
body's effective group from Low to **High** (or People, if combined
with `CalculateOffsetByPeopleGroup`). These bodies pass High-group
action numbers from CC already, so re-applying the Low → High remap
would **double-translate**.

In our CC install (`mobtypes.txt`), 21 Animal bodies have this flag:
`5, 6, 23, 25, 27, 29, 34, 37, 52, 63, 64, 65, 81, 88, 97, 98, 99,
100, 127, 133, 134`. Visually identified: eagles (5), small birds
(6), bats (29), various dragons / wyverns (97, 127, 134), and a mix
of cats / quadrupeds. The shared trait: they animate with monster-
style action sets (Walk + Stand replaced by Fly etc.).

Our port checks `AnimationFlags.CalculateOffsetLowGroupExtended` and
skips the Low → High remap when set (unless `ByLowGroup` is also
set, which keeps the body in Low).

### Open mystery: 8 bytes at 0x28..0x2F

Looking at body 400 across actions: bytes at `0x28..0x2A` are three
small values usually within 2 of each other.

| action | bytes 0x28..0x2A | total frames | guess |
|---|---|---|---|
| 0 (idle) | `03 02 03` | 50 | low values for slow anim |
| 3 (run)  | `0b 0b 0b` | 50 | high values for fast cycle |
| 26       | `0d 0d 0d` | 50 | also high |

For body 216 (cow), idle action (= AMOU 01) starts with `02 02 02 00`.
For body 401 most actions start with `00 00 00 00`.

Values don't cleanly divide total-frames into directions or known
durations. **Hypothesis (unverified)**: per-direction frame counts
for a 3-bucket sub-anim layout (idle/walk/run base) used by the EC
animation timing engine. **TBD** — confirmed by Ghidra would be a
function reading bytes at amou+0x28 then dividing by direction.

The last 4 bytes (0x2C..0x2F) are usually `00 00 00 01` with rare
variants like `00 00 01 01` — looks like a per-action flags field.

## UOReader provenance

UOReader 0.8.7 (2013) by Kons — released on the Mythic-era Ultima Online
fan community. Source is on the Google Code Archive:
`https://storage.googleapis.com/google-code-archive-downloads/v2/code.google.com/kprojects/UOReader_0.8.7.zip`.
The animation viewer there is described as ported from `KRFrameViewer`,
made by Kons and Wim during the Kingdom Reborn era — so this format has
been continuous from KR through EC.
