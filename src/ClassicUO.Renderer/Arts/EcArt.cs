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

        /// <summary>
        /// Whether EC art is loaded and the renderer is allowed to swap it in.
        /// Settable at runtime — flip this to A/B compare CC vs EC live.
        /// </summary>
        public bool IsEnabled { get; set; }
        public bool CanEnable { get; }

        /// <summary>
        /// Diagnostic mode: when on, statics with no EC art are simply not
        /// drawn. Combined with <see cref="IsEnabled"/> this makes it visually
        /// obvious which tiles in the world actually have EC sprites.
        /// </summary>
        public bool DiagnosticMode { get; set; }

        public EcArt(EcArtLoader arts, EcTileArtLoader tileart, GraphicsDevice device)
        {
            _arts = arts;
            _tileart = tileart;
            _device = device;
            CanEnable = arts != null && device != null && arts.IsEnabled;
            // Initial state is set by the caller (Client.cs), gated on the setting.
            IsEnabled = false;
        }

        /// <summary>Toggle the EC swap on/off. Returns the new state.</summary>
        public bool Toggle()
        {
            if (!CanEnable) return false;
            IsEnabled = !IsEnabled;
            Log.Info($"EcArt counters: hit={HitCount}  miss(noRecord)={MissNoRecordCount}  "
                     + $"miss(noSpriteId)={MissNoSpriteIdCount}  miss(noDDS)={MissDdsCount}");
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
            if (!IsEnabled) return false;
            if (_missing.Contains(artId)) return false;
            if (_cache.TryGetValue(artId, out art)) return art.IsValid;

            // Direct art_id lookup. EC convention (from KonssnoK's Unity
            // reference): only use HD when EcImage (0x4D) is populated;
            // otherwise legacy. Match that here.
            EcTileArtData meta = null;
            _tileart?.TryGet(artId, out meta);
            bool wantHd = meta != null
                          && meta.EcImage.Width  > 0
                          && meta.EcImage.Height > 0;

            byte[] dds = null;
            bool fromHd = false;
            if (wantHd)
            {
                _arts.TryGetHdByArtId(artId, out dds);
                if (dds != null) fromHd = true;
            }
            if (dds == null)
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
            if (_arts.TryGetMaskByArtId(artId, out byte[] maskDds))
            {
                try
                {
                    var converted = ApplyHueMaskFromDds(dds, maskDds, tex.Width, tex.Height);
                    if (converted != null)
                    {
                        tex.Dispose();
                        tex = converted;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"EcArt: mask preprocessing failed for id {artId}: {ex.Message}");
                }
            }

            // Source rect + world anchor:
            //   Legacy: whole canvas, no offset.
            //   HD: source = (Xstart, 0, Xend - Xstart, Yend) per Unity ref
            //       (Ystart isn't used for the crop). Anchor = (offX, offY)
            //       from the same 6-int block — these are in 64-pixel units
            //       and need scaling to CC's 44-pixel cells.
            int srcX = 0, srcY = 0, srcW = tex.Width, srcH = tex.Height;
            int anchorX = 0, anchorY = 0;
            Vector2 scale = Vector2.One;

            if (fromHd && meta != null && meta.EcImage.Width > 0 && meta.EcImage.Height > 0)
            {
                var img = meta.EcImage;
                // Per Ghidra FUN_00459390 HD branch: rect bounds are
                // INCLUSIVE — width = Xend - Xstart + 1, height = Yend - Ystart + 1.
                int x0 = img.X0, y0 = img.Y0;
                int x1 = img.X1 + 1, y1 = img.Y1 + 1;
                if (x0 >= 0 && y0 >= 0 && x1 <= tex.Width && y1 <= tex.Height)
                {
                    srcX = x0;
                    srcY = y0;
                    srcW = x1 - x0;
                    srcH = y1 - y0;
                    anchorX = img.PixelsXOffset;
                    anchorY = img.PixelsYOffset;
                    scale = new Vector2(44f / 64f, 44f / 64f);
                }
            }

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
            if (HitCount <= 60)
            {
                int itemId = artId >= 0x4000 ? artId - 0x4000 : artId;
                Log.Info($"EcArt HIT #{HitCount}: art_id={artId} (item_id={itemId}) "
                         + $"{(fromHd ? "HD" : "Legacy")} "
                         + $"dds={tex.Width}x{tex.Height} "
                         + $"src=({srcX},{srcY},{srcW}x{srcH})");
            }
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
