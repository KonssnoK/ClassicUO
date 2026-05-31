// SPDX-License-Identifier: BSD-2-Clause
//
// EC art texture cache. Sits next to ClassicUO.Renderer.Arts.Art and provides
// HD/legacy-DDS replacements for static items at draw time.
//
// Decoding pipeline:
//   1. EcArtLoader.TryGetDds(artId)  -> byte[] of a complete DDS file
//   2. Texture2D.DDSFromStreamEXT    -> GPU texture
//   3. Cached on first use, evicted on Dispose.
//
// The anchor offset (where the texture sits relative to the world cell) comes
// from the tileart record's EcImage / LegacyImage fields (six int32: x0/y0/x1/y1/dx/dy).
// See tools/ec_research/docs/tileart_VERIFIED.md.

using ClassicUO.Assets;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.IO;

namespace ClassicUO.Renderer.Arts
{
    /// <summary>
    /// Which tileart source the renderer should use for static placement.
    /// All three sources coexist in the same install — this just picks
    /// which one wins per draw call.
    /// </summary>
    public enum EcArtMode
    {
        /// <summary>Skip EC entirely; renderer uses art.mul / artLegacyMUL.uop.</summary>
        ClassicMul = 0,

        /// <summary>
        /// Kingdom-Reborn-era pipeline: big HD master from Texture.uop
        /// (`build/worldart/{id}.dds`), EcImage sub-rect crop, signed dx/dy
        /// canvas padding, partial-hue mask. These are the larger upscaled
        /// sprites; KR rendered statics this way. Falls back to LegacyTexture.uop
        /// when a tile has no HD entry.
        /// </summary>
        UopKR = 1,

        /// <summary>
        /// Enhanced-Client pipeline as actually shipped: small 2D sprites from
        /// LegacyTexture.uop (`build/tileartlegacy/{id}.dds`). These are the
        /// closest-to-CC-looking sprites — EC went back to flat 2D for statics
        /// after KR, keeping HD masters only for the chunked-mesh terrain.
        /// No EcImage crop, no hue mask; drawn bottom-center on the cell.
        /// </summary>
        UopEC = 2,
    }

    public struct EcRenderArt
    {
        public Texture2D Texture;
        // Sub-rect of Texture that holds the actual sprite content. For
        // legacy: the whole DDS canvas. For HD: the alpha-trimmed bbox.
        public Rectangle Source;
        public int Width;
        public int Height;
        public int AnchorX;
        public int AnchorY;
        public bool FromHd;
        // Per-tile draw scale (1.0 for legacy; HD uses 1/tileart.0x10 etc.
        // to fit HD pixels into CC-equivalent world dimensions).
        public Vector2 Scale;

        public bool IsValid => Texture != null;
        public static EcRenderArt Empty;
    }

    public sealed class EcArt : IDisposable
    {
        private readonly EcArtLoader _arts;
        private readonly EcTileArtLoader _tileart;
        private readonly GraphicsDevice _device;
        private readonly Dictionary<int, EcRenderArt> _cache = new();
        // Per-id negative cache: ids known not to have EC art (avoids re-trying).
        private readonly HashSet<int> _missing = new();
        // Diagnostic counters; reported on Toggle().
        public int HitCount;
        public int MissNoRecordCount;
        public int MissNoSpriteIdCount;
        public int MissDdsCount;

        // True when the cached texture went through full mask preprocessing
        // (= shader's strict R==G==B partial-hue test will work).
        // When false, the renderer should use SHADER_HUED instead, matching
        // EC's "no-mask = full hue" behaviour in shader_04.hlsl.
        public readonly System.Collections.Generic.HashSet<int> _hasMask = new();
        public bool HasHueMask(int artId) => _hasMask.Contains(artId);

        // Per-tile "master" resolution. Many EC tile_ids don't ship their own
        // build/worldart/{id}.dds — they're meant to crop a sub-rect of a
        // SHARED master texture that belongs to a sibling tile_id with the
        // same SUB_9_7 source name (string_dictionary entry). Per Ghidra
        // FUN_00459390: the asset's rect in `in_EAX[0..5]` comes from
        // EcImage at 0x4D (HD path) with X1+1/Y1+1 (inclusive→exclusive);
        // when EcImage is unpopulated the engine falls back to (0,0,44,44).
        // FUN_0051a840 then fit-to-44 scales the crop when oversized.
        //
        // We build a tile_id → master_art_id map at startup by walking 0..16383
        // forward, tracking "for this StringId, what was the last tile_id with
        // an own HD DDS?". Tiles between HD owners point back to the previous
        // owner; tiles WITH HD become a new master (so e.g. tile 1414's own
        // HD ends the slate-roof group and starts a new one).
        private System.Collections.Generic.Dictionary<int, int> _masterMap;
        private readonly object _masterInitLock = new();

        public bool IsAbsorbedByHdSibling(int artId)
        {
            // Banner-style absorption: a tile gets "absorbed" when (a) it
            // has no HD of its own, (b) a sibling in the same sd_off group
            // ships HD (i.e. there IS a master), AND (c) this tile has no
            // EcImage rect — without a crop, there's nothing meaningful to
            // sample from the master. The HD-owning sibling renders the
            // whole multi-cell sprite; this absorbed cell should render
            // nothing. Without this check, tile 5650 would render the
            // top-left 44×44 of 5649's full banner, duplicating content.
            //
            // ONLY applies in UopKR (HD-master) mode. UopEC mode pulls each
            // tile's own legacy DDS independently — no master texture, no
            // duplication risk, so absorbing would just hide valid tiles.
            if (_mode != EcArtMode.UopKR) return false;
            if (_tileart == null || _arts == null) return false;
            if (_arts.TryGetHdByArtId(artId, out _)) return false;  // own HD
            if (!_tileart.TryGet(artId, out var meta) || meta == null) return false;
            if (meta.EcImage.IsPopulated) return false;             // crop specified
            int master = GetMasterArtId(artId);
            return master >= 0 && master != artId;
        }

        private void EnsureMasterMap()
        {
            if (_masterMap != null) return;
            lock (_masterInitLock)
            {
                if (_masterMap != null) return;
                var map = new System.Collections.Generic.Dictionary<int, int>();
                // Group by RESOLVED string text, not by raw sd_off — each
                // tile points to its own offset that lands within the same
                // Pascal-style string entry. tile 1407 sd_off=48860 and
                // tile 1408 sd_off=48864 both resolve to
                // "Data\\TileArtEnhanced\\500.tga" — they must share a master.
                var dict = _tileart.FileManager.EcStringDictionary;
                var lastHdOwnerForString = new System.Collections.Generic.Dictionary<string, int>();
                for (int item = 0; item <= 0x3FFF; item++)
                {
                    int aid = 0x4000 + item;
                    if (!_tileart.TryGet(aid, out var meta) || meta == null) continue;
                    string key = dict?.GetStringAtOffset((int)meta.StringId);
                    if (string.IsNullOrEmpty(key)) continue;
                    bool hasHd = _arts.TryGetHdByArtId(aid, out _);
                    if (hasHd)
                    {
                        lastHdOwnerForString[key] = aid;
                        map[aid] = aid;   // own master
                    }
                    else if (lastHdOwnerForString.TryGetValue(key, out int masterAid))
                    {
                        map[aid] = masterAid;
                    }
                    // else: no master; tile will use legacy fallback
                }
                _masterMap = map;
            }
        }

        /// <summary>
        /// For tiles in a shared-master group, returns the art_id whose own
        /// HD DDS holds the master texture. Returns the input artId if the
        /// tile itself owns an HD; returns -1 when no master exists in this
        /// build (the tile should fall back to legacy).
        /// </summary>
        public int GetMasterArtId(int artId)
        {
            if (!IsEnabled || _tileart == null || _arts == null) return -1;
            EnsureMasterMap();
            return _masterMap.TryGetValue(artId, out int master) ? master : -1;
        }

        /// <summary>
        /// Which tileart source the renderer is currently using. Changing the
        /// mode invalidates the per-art cache (each mode produces different
        /// textures). When <see cref="CanEnable"/> is false the renderer is
        /// locked to <see cref="EcArtMode.ClassicMul"/>.
        /// </summary>
        private EcArtMode _mode = EcArtMode.ClassicMul;
        public EcArtMode Mode
        {
            get => _mode;
            set
            {
                if (!CanEnable) value = EcArtMode.ClassicMul;
                if (_mode == value) return;
                _mode = value;
                InvalidateCache();
            }
        }

        /// <summary>True while any non-classic mode is selected.</summary>
        public bool IsEnabled
        {
            get => _mode != EcArtMode.ClassicMul;
            set => Mode = value ? EcArtMode.UopEC : EcArtMode.ClassicMul;
        }

        public bool CanEnable { get; }

        private void InvalidateCache()
        {
            foreach (var v in _cache.Values) v.Texture?.Dispose();
            _cache.Clear();
            _missing.Clear();
            _hasMask.Clear();
        }

        /// <summary>
        /// Diagnostic mode: when on, statics with no EC art are simply not
        /// drawn. Combined with <see cref="IsEnabled"/> this makes it visually
        /// obvious which tiles in the world actually have EC sprites.
        /// </summary>
        public bool DiagnosticMode { get; set; }
        /// <summary>
        /// When on, tint every EC-rendered static red so the user can see
        /// exactly which tiles are using EC art and where each lands.
        /// </summary>
        public bool OutlineMode { get; set; }

        public EcArt(EcArtLoader arts, EcTileArtLoader tileart, GraphicsDevice device)
        {
            _arts = arts;
            _tileart = tileart;
            _device = device;
            CanEnable = arts != null && device != null && arts.IsEnabled;
            // Initial state is set by the caller (Client.cs), gated on the setting.
            _mode = EcArtMode.ClassicMul;
        }

        /// <summary>
        /// Cycle to the next tileart mode (Classic → KR → EC → Classic).
        /// Returns the new mode. When <see cref="CanEnable"/> is false this
        /// is a no-op and always returns <see cref="EcArtMode.ClassicMul"/>.
        /// </summary>
        public EcArtMode CycleMode()
        {
            if (!CanEnable) return EcArtMode.ClassicMul;
            Log.Info($"EcArt counters: hit={HitCount}  miss(noRecord)={MissNoRecordCount}  "
                     + $"miss(noSpriteId)={MissNoSpriteIdCount}  miss(noDDS)={MissDdsCount}");
            Mode = _mode switch
            {
                EcArtMode.ClassicMul => EcArtMode.UopKR,
                EcArtMode.UopKR      => EcArtMode.UopEC,
                _                    => EcArtMode.ClassicMul,
            };
            return _mode;
        }

        /// <summary>Legacy two-state toggle: flips between Classic and full EC.</summary>
        public bool Toggle()
        {
            if (!CanEnable) return false;
            IsEnabled = !IsEnabled;
            return IsEnabled;
        }

        /// <summary>
        /// Resolves an EC-art replacement for the given static art id.
        ///
        /// Resolution chain (matches the EC engine's behaviour, verified via
        /// tileart.uop record dumps in tools/ec_research):
        ///   1. Look up the tile_id in tileart.uop to get the per-tile record.
        ///   2. Pull the sprite id from the SUB_9_7 reference (WorldArt /
        ///      TileArtLegacy / TileArtEnhanced groups; the high 16 bits of
        ///      each entry's `b` u32).
        ///   3. Hash that sprite id into Texture.uop / LegacyTexture.uop to
        ///      get the DDS bytes.
        ///   4. Pull bounds + anchor from the record's image-offset fields
        ///      (EcImage when fromHd, LegacyImage otherwise).
        /// Returns false when any step has no data; the renderer falls back
        /// to the classic art path.
        /// </summary>
        public bool TryGet(int artId, out EcRenderArt art)
        {
            art = EcRenderArt.Empty;
            if (_mode == EcArtMode.ClassicMul) return false;
            if (_missing.Contains(artId)) return false;
            if (_cache.TryGetValue(artId, out art)) return art.IsValid;

            // EC mode (as actually shipped): skip HD master + EcImage crop +
            // mask. Use only the flat 2D sprite from LegacyTexture.uop's
            // tileartlegacy entry, drawn bottom-center on the cell — same
            // anchor math as CC.
            if (_mode == EcArtMode.UopEC)
                return TryGetLegacyOnly(artId, out art);

            // Prefer HD when DDS exists; fall back to legacy.
            // EcImage rect represents only a SUB-PIECE of the HD canvas
            // (EC treats multi-cell objects as separate tiles, while CC
            // shows the whole object in one tile). So we don't use the
            // EcImage rect — we alpha-trim the whole HD canvas and render
            // that. The render code aligns by CC content bbox.
            EcTileArtData meta = null;
            _tileart?.TryGet(artId, out meta);

            // HD resolution: this tile's master may be a sibling's HD DDS
            // (FUN_00459390 / FUN_0051a840 — the engine picks the rect from
            // EcImage but the texture handle is shared across the group).
            int masterArtId = GetMasterArtId(artId);

            byte[] dds = null;
            bool fromHd = false;
            bool isTerrainTile = false;
            int hdLoadedFromArtId = -1;
            if (masterArtId >= 0 && _arts.TryGetHdByArtId(masterArtId, out byte[] hdDds))
            {
                // EC renders fully-opaque tileable masters (slate roof,
                // water, plain floors) through its KR-era chunked terrain
                // mesh: a 32×32-cell mesh in world space sampled with
                // world-position UVs (`uv = world_xz / 32`), so the texture
                // pattern flows seamlessly across cells. See
                // docs/ec_renderer_VERIFIED.md for the APItrace findings.
                //
                // CUO is a 2D sprite batcher — we can't reproduce that
                // without a separate render pipeline (TODO: chunked-mesh
                // terrain renderer). For now: when the master is fully
                // opaque, skip the HD path entirely and fall back to the
                // tile's legacy DDS, which is already pre-iso-projected as
                // a 64×64 diamond (CC-equivalent look at CC resolution).
                if (IsFullyOpaqueDds(hdDds))
                {
                    _arts.TryGetLegacyByArtId(artId, out dds);
                }
                else
                {
                    dds = hdDds;
                    fromHd = true;
                    hdLoadedFromArtId = masterArtId;
                }
            }
            else
            {
                _arts.TryGetLegacyByArtId(artId, out dds);
            }
            if (dds == null)
            {
                _missing.Add(artId);
                MissDdsCount++;
                return false;
            }

            Texture2D tex;
            try
            {
                using var ms = new MemoryStream(dds, writable: false);
                tex = Texture2D.DDSFromStreamEXT(_device, ms);
            }
            catch (Exception ex)
            {
                Log.Warn($"EcArt: DDS decode failed for id {artId}: {ex.Message}");
                _missing.Add(artId);
                return false;
            }

            // EC's mask-based partial-hue scheme: a per-sprite mask DDS
            // (alpha channel) marks which pixels are hueable. CPU-decode the
            // DXT5 color and mask DDS bytes, apply the mask, and rebuild as
            // an uncompressed Color texture so the existing shader's strict
            // R==G==B partial-hue test produces the same effect as EC's
            // mask lerp:
            //   - mask alpha > 0  → snap pixel to R=G=B (shader will hue it)
            //   - mask alpha == 0 → ensure R != G (shader leaves it alone)
            // (FNA's Texture2D.GetData on a DXT5 surface returns the raw
            // compressed blocks, not decoded pixels — that's why we decode
            // the DDS bytes directly here.)
            bool maskApplied = false;
            if (_arts.TryGetMaskByArtId(artId, out byte[] maskDds))
            {
                try
                {
                    var converted = ApplyHueMaskFromDds(dds, maskDds, tex.Width, tex.Height);
                    if (converted != null)
                    {
                        tex.Dispose();
                        tex = converted;
                        maskApplied = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"EcArt: mask preprocessing failed for id {artId}: {ex.Message}");
                }
            }
            if (maskApplied)
                _hasMask.Add(artId);
            else
                _hasMask.Remove(artId);

            // Source rect:
            //   Legacy: whole canvas (canvas-origin shares with CC).
            //   HD: alpha-trim the bbox so we know where the actual content
            //       sits in the larger HD canvas. The renderer scales the
            //       bbox to match CC's canvas dimensions per-tile.
            int srcX = 0, srcY = 0, srcW = tex.Width, srcH = tex.Height;
            int anchorX = 0, anchorY = 0;
            Vector2 scale = Vector2.One;

            if (fromHd)
            {
                if (meta != null && meta.EcImage.IsPopulated && meta.LegacyImage.IsPopulated
                    && meta.EcImage.X1 > 0 && meta.EcImage.Y1 > 0
                    && meta.EcImage.X0 < tex.Width && meta.EcImage.Y0 < tex.Height
                    && (meta.EcImage.X1 + 1) > meta.EcImage.X0
                    && (meta.EcImage.Y1 + 1) > meta.EcImage.Y0)
                {
                    // KR HD with explicit EcImage: sub-rect of the master,
                    // +1 on X1/Y1 for exclusive bounds. Per UOReader, the
                    // 5th/6th ints (PixelsXOffset, PixelsYOffset) carry
                    // signed canvas-padding around the sprite, collapsed
                    // into a (shiftX, shiftY) offset from bottom-center
                    // anchor (same formula as the EC legacy path).
                    int x0 = System.Math.Clamp(meta.EcImage.X0, 0, tex.Width - 1);
                    int y0 = System.Math.Clamp(meta.EcImage.Y0, 0, tex.Height - 1);
                    int x1 = System.Math.Clamp(meta.EcImage.X1 + 1, x0 + 1, tex.Width);
                    int y1 = System.Math.Clamp(meta.EcImage.Y1 + 1, y0 + 1, tex.Height);
                    srcX = x0; srcY = y0; srcW = x1 - x0; srcH = y1 - y0;

                    // UNIFORM HD→CC scale: the HD master texture pixel pitch
                    // is 1.5× CC's (per the EC binary constant DAT_00c853b4 =
                    // 1.5), so the inverse is 2/3. A per-axis ratio derived
                    // from LegacyImage/EcImage dimensions causes aspect-ratio
                    // distortion because EC and CC show DIFFERENT content
                    // extents per tile (EC walls extend taller; banners are
                    // wider in HD), not different pixel pitch.
                    const float HD_TO_CC = 1f / 1.5f;
                    scale = new Vector2(HD_TO_CC, HD_TO_CC);

                    // dx/dy from EcImage — must be applied to position
                    // hanging items (signs, banners, ceiling fixtures)
                    // above the cell floor. Tile 2967 (wooden signpost)
                    // has dy=-110: without applying it the sign falls to
                    // floor level instead of hanging at sign height.
                    // Walls/statues with small dx values will look "a bit
                    // to the right" of where CC would put them — that's
                    // EC's actual intended placement (the HD sprite content
                    // is shifted within its canvas per the dx encoding).
                    int dx = meta.EcImage.PixelsXOffset;
                    int dy = meta.EcImage.PixelsYOffset;
                    int unscaledShiftX = System.Math.Max(dx, 0) - System.Math.Abs(dx) / 2;
                    int unscaledShiftY = System.Math.Max(dy, 0) - System.Math.Abs(dy);
                    anchorX = (int)(unscaledShiftX * HD_TO_CC);
                    anchorY = (int)(unscaledShiftY * HD_TO_CC);
                }
                else
                {
                    // EcImage unpopulated (or degenerate with X1/Y1 = 0).
                    // The tile owns an HD master but doesn't carry a crop
                    // rect — typical for standalone walls/doors whose HD
                    // master is just this one tile's content. Alpha-trim
                    // the HD canvas to find visible bbox, then scale it to
                    // CC pixel pitch using the LegacyImage W/H ratio when
                    // available, falling back to HD_TO_CC = 2/3.
                    (srcX, srcY, srcW, srcH) = ComputeVisibleBoundsFromDds(dds, tex.Width, tex.Height);
                    if (meta != null && meta.LegacyImage.IsPopulated)
                    {
                        int legW = meta.LegacyImage.X1 - meta.LegacyImage.X0;
                        int legH = meta.LegacyImage.Y1 - meta.LegacyImage.Y0;
                        float sx = legW > 0 && srcW > 0 ? (float)legW / srcW : 2f / 3f;
                        float sy = legH > 0 && srcH > 0 ? (float)legH / srcH : 2f / 3f;
                        scale = new Vector2(sx, sy);
                    }
                    else
                    {
                        scale = new Vector2(2f / 3f, 2f / 3f);
                    }
                    // CC-bbox alignment at draw time: the HD content's
                    // visible bbox is positioned where CC content's bbox
                    // would land. The renderer reads this via a marker
                    // (FromHd + meta.LegacyImage tells the renderer to
                    // align by CC content bbox, not by canvas bottom-center).
                }
            }
            // Legacy: kept on full-canvas src. FUN_0051af20 reads
            // LegacyImage (X1-X0, Y1-Y0) and an optional fit-to-44 scale
            // but using those as a raw source crop didn't help the roof
            // rendering — the rect likely represents display dimensions
            // / hit-test, not where to crop in the DDS.

            art = new EcRenderArt
            {
                Texture = tex,
                Source = new Rectangle(srcX, srcY, srcW, srcH),
                Width = srcW,
                Height = srcH,
                AnchorX = anchorX,
                AnchorY = anchorY,
                FromHd = fromHd,
                Scale = scale,
            };
            _cache[artId] = art;

            HitCount++;
            return true;
        }

        /// <summary>
        /// EC-mode load: pull `build/tileartlegacy/{id}.dds` (the flat 2D
        /// sprite EC uses for statics) and return it for rendering with
        /// CC's standard anchor math. The renderer uses the CC art's
        /// canvas dimensions (artInfo.UV) — NOT the EC DDS dimensions —
        /// so the POT-padded DDS slots into the same world-position as
        /// CC's equivalent art would. Content lives at top-left of the
        /// DDS; transparent POT padding is invisible.
        ///
        /// We tried using LegacyImage as a crop rect + dx/dy padding here
        /// (matches UOReader's preview-rendering math), but it diverges
        /// from CC anchor for tiles with content-in-middle layouts (e.g.
        /// roof tile 1475 with elevation padding). The simpler "draw full
        /// DDS at CC anchor" path matches CC for the vast majority of
        /// statics — same approach as commit 5e0475334.
        /// </summary>
        private bool TryGetLegacyOnly(int artId, out EcRenderArt art)
        {
            art = EcRenderArt.Empty;
            if (!_arts.TryGetLegacyByArtId(artId, out byte[] dds))
            {
                _missing.Add(artId);
                MissDdsCount++;
                return false;
            }

            Texture2D tex;
            try
            {
                using var ms = new MemoryStream(dds, writable: false);
                tex = Texture2D.DDSFromStreamEXT(_device, ms);
            }
            catch (Exception ex)
            {
                Log.Warn($"EcArt(EC): DDS decode failed for id {artId}: {ex.Message}");
                _missing.Add(artId);
                return false;
            }

            art = new EcRenderArt
            {
                Texture = tex,
                Source = new Rectangle(0, 0, tex.Width, tex.Height),
                Width = tex.Width,
                Height = tex.Height,
                AnchorX = 0,
                AnchorY = 0,
                FromHd = false,
                Scale = Vector2.One,
            };
            _cache[artId] = art;
            HitCount++;
            return true;
        }

        // Scratch buffer reused across decode calls — DDS textures are at
        // most ~256x256 here.
        private Color[] _scanBuf;
        private Color[] _maskBuf;

        /// <summary>
        /// Decompresses the color and mask DXT5 DDSes on the CPU, applies
        /// the mask-driven rewrites, and returns a fresh uncompressed
        /// (<see cref="SurfaceFormat.Color"/>) texture.
        /// </summary>
        private Texture2D ApplyHueMaskFromDds(byte[] colorDds, byte[] maskDds, int width, int height)
        {
            int total = width * height;
            byte[] color = DecodeDxt5Rgba(colorDds, width, height);
            byte[] mask  = DecodeDxt5Rgba(maskDds,  width, height);
            if (color == null || mask == null) return null;

            if (_scanBuf == null || _scanBuf.Length < total)
                _scanBuf = new Color[total];

            for (int i = 0; i < total; i++)
            {
                int p = i * 4;
                byte r = color[p], g = color[p + 1], b = color[p + 2], a = color[p + 3];
                if (a != 0)
                {
                    byte m = mask[p + 3];
                    if (m > 0)
                    {
                        byte avg = (byte)(((int)r + g + b) / 3);
                        r = g = b = avg;
                    }
                    else if (r == g && r == b)
                    {
                        if (r < 255) r = (byte)(r + 1);
                        else         r = (byte)(r - 1);
                    }
                }
                _scanBuf[i] = new Color(r, g, b, a);
            }

            var converted = new Texture2D(_device, width, height, false, SurfaceFormat.Color);
            converted.SetData(_scanBuf, 0, total);
            return converted;
        }

        /// <summary>
        /// Fast all-opaque test for a DXT5 DDS. In DXT5 each 4×4 block's
        /// alpha is encoded by two endpoint bytes a0/a1 (offsets 0/1 of the
        /// block); when both are 255 the entire block is opaque regardless
        /// of the index bits. Scanning only those two bytes per block is
        /// ~32× cheaper than full decoding and is sufficient to detect
        /// tileable terrain textures (e.g. tile 1407 slate roof at 256×256
        /// is 100 % opaque).
        /// </summary>
        private static bool IsFullyOpaqueDds(byte[] dds)
        {
            if (dds == null || dds.Length < 128) return false;
            if (dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ') return false;
            // DXT1: 4 bits/pixel, no alpha channel — always fully opaque.
            // (Slate-roof master tile 1407 ships as DXT1.)
            if (dds[84] == 'D' && dds[85] == 'X' && dds[86] == 'T' && dds[87] == '1')
                return true;
            // DXT5: each 16-byte block starts with a0,a1 alpha endpoints.
            // When both are 255 the block is opaque regardless of indices;
            // a single block with anything else means the texture has alpha.
            if (dds[84] != 'D' || dds[85] != 'X' || dds[86] != 'T' || dds[87] != '5') return false;
            for (int off = 128; off + 16 <= dds.Length; off += 16)
            {
                if (dds[off] != 255 || dds[off + 1] != 255) return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the alpha-trimmed visible bbox of a DXT5 DDS. Decodes
        /// the DDS on the CPU (FNA's GetData on a compressed surface
        /// returns raw block bytes).
        /// </summary>
        private static (int X, int Y, int W, int H) ComputeVisibleBoundsFromDds(byte[] dds, int width, int height)
        {
            byte[] rgba = DecodeDxt5Rgba(dds, width, height);
            if (rgba == null) return (0, 0, width, height);
            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                int row = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    if (rgba[row + x * 4 + 3] != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return (0, 0, width, height);
            return (minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        /// <summary>
        /// Parses a DDS file containing a single DXT5-compressed mip and
        /// returns an RGBA8 byte buffer (width*height*4 bytes). Returns null
        /// on parse failure (wrong magic / unexpected format).
        /// </summary>
        private static byte[] DecodeDxt5Rgba(byte[] dds, int width, int height)
        {
            if (dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ')
                return null;
            // fourCC at offset 84..87
            if (dds[84] != 'D' || dds[85] != 'X' || dds[86] != 'T' || dds[87] != '5')
                return null;

            byte[] rgba = new byte[width * height * 4];
            int blocksX = (width + 3) / 4;
            int blocksY = (height + 3) / 4;
            int offset = 128;

            byte[] aPalette = new byte[8];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    // Alpha block: 2 endpoints + 48 bits of 3-bit indices
                    byte a0 = dds[offset + 0];
                    byte a1 = dds[offset + 1];
                    aPalette[0] = a0;
                    aPalette[1] = a1;
                    if (a0 > a1)
                    {
                        aPalette[2] = (byte)((6 * a0 + 1 * a1) / 7);
                        aPalette[3] = (byte)((5 * a0 + 2 * a1) / 7);
                        aPalette[4] = (byte)((4 * a0 + 3 * a1) / 7);
                        aPalette[5] = (byte)((3 * a0 + 4 * a1) / 7);
                        aPalette[6] = (byte)((2 * a0 + 5 * a1) / 7);
                        aPalette[7] = (byte)((1 * a0 + 6 * a1) / 7);
                    }
                    else
                    {
                        aPalette[2] = (byte)((4 * a0 + 1 * a1) / 5);
                        aPalette[3] = (byte)((3 * a0 + 2 * a1) / 5);
                        aPalette[4] = (byte)((2 * a0 + 3 * a1) / 5);
                        aPalette[5] = (byte)((1 * a0 + 4 * a1) / 5);
                        aPalette[6] = 0;
                        aPalette[7] = 255;
                    }
                    ulong aBits = (ulong)dds[offset + 2]
                                | ((ulong)dds[offset + 3] << 8)
                                | ((ulong)dds[offset + 4] << 16)
                                | ((ulong)dds[offset + 5] << 24)
                                | ((ulong)dds[offset + 6] << 32)
                                | ((ulong)dds[offset + 7] << 40);

                    // Color block: 2 RGB565 endpoints + 32 bits of 2-bit indices
                    ushort c0 = (ushort)(dds[offset + 8] | (dds[offset + 9] << 8));
                    ushort c1 = (ushort)(dds[offset + 10] | (dds[offset + 11] << 8));
                    int r0 = ((c0 >> 11) & 0x1F) * 255 / 31;
                    int g0 = ((c0 >> 5) & 0x3F) * 255 / 63;
                    int b0 = (c0 & 0x1F) * 255 / 31;
                    int r1 = ((c1 >> 11) & 0x1F) * 255 / 31;
                    int g1 = ((c1 >> 5) & 0x3F) * 255 / 63;
                    int b1 = (c1 & 0x1F) * 255 / 31;
                    uint cBits = (uint)(dds[offset + 12]
                                       | (dds[offset + 13] << 8)
                                       | (dds[offset + 14] << 16)
                                       | (dds[offset + 15] << 24));

                    for (int py = 0; py < 4; py++)
                    {
                        for (int px = 0; px < 4; px++)
                        {
                            int idx = py * 4 + px;
                            int cIdx = (int)((cBits >> (idx * 2)) & 0x3);
                            int aIdx = (int)((aBits >> (idx * 3)) & 0x7);

                            int r, g, b;
                            switch (cIdx)
                            {
                                case 0: r = r0; g = g0; b = b0; break;
                                case 1: r = r1; g = g1; b = b1; break;
                                case 2: r = (2 * r0 + r1) / 3; g = (2 * g0 + g1) / 3; b = (2 * b0 + b1) / 3; break;
                                default: r = (r0 + 2 * r1) / 3; g = (g0 + 2 * g1) / 3; b = (b0 + 2 * b1) / 3; break;
                            }

                            int x = bx * 4 + px;
                            int y = by * 4 + py;
                            if (x >= width || y >= height) continue;
                            int dstOff = (y * width + x) * 4;
                            rgba[dstOff + 0] = (byte)r;
                            rgba[dstOff + 1] = (byte)g;
                            rgba[dstOff + 2] = (byte)b;
                            rgba[dstOff + 3] = aPalette[aIdx];
                        }
                    }
                    offset += 16;
                }
            }
            return rgba;
        }

        private (int X, int Y, int W, int H) ComputeVisibleBounds(Texture2D tex)
        {
            int w = tex.Width, h = tex.Height;
            int total = w * h;
            if (_scanBuf == null || _scanBuf.Length < total)
                _scanBuf = new Color[total];
            tex.GetData(_scanBuf, 0, total);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (_scanBuf[row + x].A != 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return (0, 0, w, h);   // fully transparent — keep whole
            return (minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public void Dispose()
        {
            foreach (var kv in _cache)
            {
                kv.Value.Texture?.Dispose();
            }
            _cache.Clear();
            _missing.Clear();
        }
    }
}
