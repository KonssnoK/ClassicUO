// SPDX-License-Identifier: BSD-2-Clause
//
// Enhanced-Client animation frame decoder and texture cache.
//
// AMOU format: tools/ec_research/docs/AnimationFrame_AMOU.md
// Reference implementation: tools/ec_research/scripts/amou_decode_verified.py
// (ported from UOReader 0.8.7 by Kons, who in turn ported it from
// the Kingdom Reborn era KRFrameViewer).
//
// Each .uop entry under build/animationframe/{body:D6}/{action:D2}.bin
// holds: 44 B header, a palette, a frame index table, and a stream of
// anti-aliased RLE pixel data — one slice per frame.
//
// This class exposes a Texture2D + anchor offset per (body, action,
// frameIndex). Direction handling (frames are concatenated per direction
// across the per-action file, similar to CC UOP anim) is left to the
// caller; callers that don't know yet treat the file as a flat array
// of frames in source order.

using ClassicUO.Assets;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Renderer.Animations
{
    public struct EcAnimFrame
    {
        // Decoded RGBA pixels, row-major, Width × Height — ready for atlas
        // upload via the same path CC frames use.
        public uint[] Pixels;
        public int Width;
        public int Height;
        // Top-left of the frame's local bbox, in body-local pixel coords
        // (body anchor at origin; Y grows downward in screen space, so the
        // head sits at a more-negative Y than the feet).
        public short InitX;
        public short InitY;
        // CC-convention anchor used by MobileView / ItemView:
        //   screen_x = world_pos.X - CenterX                (left edge)
        //   screen_y = world_pos.Y - (Height + CenterY)     (top edge)
        // Derived from AMOU per-frame bbox:
        //   CenterX = -InitX
        //   CenterY = -(InitY + Height)   (= -EndY)
        public short CenterX;
        public short CenterY;
        public bool IsValid => Pixels != null && Width > 0 && Height > 0;
    }

    public sealed class EcAnimation : IDisposable
    {
        private readonly EcAnimationLoader _loader;
        private readonly GraphicsDevice _device;
        // Keyed by (body << 8) | action.
        private readonly Dictionary<int, EcAnimFrame[]> _cache = new();
        private readonly HashSet<int> _missing = new();

        public EcAnimation(EcAnimationLoader loader, GraphicsDevice device)
        {
            _loader = loader;
            _device = device;
            CanEnable = loader != null && device != null && loader.IsEnabled;
        }

        /// <summary>True when EC anim UOPs were found at startup.</summary>
        public bool CanEnable { get; }

        /// <summary>
        /// Master switch: when true, the animation pipeline consults this
        /// cache before falling back to CC (anim.mul / AnimationFrame.uop).
        /// When false (default), CC pipeline runs as-is.
        /// </summary>
        public bool UseEc { get; set; }

        /// <summary>Toggle EC animation source. Returns the new state.</summary>
        public bool Toggle()
        {
            if (!CanEnable) return false;
            UseEc = !UseEc;
            return UseEc;
        }

        public bool IsEnabled => CanEnable && UseEc;

        public bool TryGetFrames(int body, int action, out EcAnimFrame[] frames)
        {
            int key = (body << 8) | (action & 0xFF);
            if (_cache.TryGetValue(key, out frames)) return frames != null;
            if (_missing.Contains(key)) { frames = null; return false; }

            if (!_loader.TryGet(body, action, out byte[] payload))
            {
                _missing.Add(key);
                frames = null;
                return false;
            }

            try
            {
                frames = DecodeAll(payload);
            }
            catch (Exception ex)
            {
                Log.Warn($"EcAnim: decode failed body={body} action={action}: {ex.Message}");
                _missing.Add(key);
                frames = null;
                return false;
            }

            _cache[key] = frames;
            return frames != null && frames.Length > 0;
        }

        public bool TryGetFrame(int body, int action, int frameIndex, out EcAnimFrame frame)
        {
            frame = default;
            if (!TryGetFrames(body, action, out var arr)) return false;
            if (frameIndex < 0 || frameIndex >= arr.Length) return false;
            frame = arr[frameIndex];
            return frame.IsValid;
        }

        // --- decoder -------------------------------------------------------

        private EcAnimFrame[] DecodeAll(byte[] data)
        {
            if (data == null || data.Length < 44) return Array.Empty<EcAnimFrame>();
            // Magic: only first 3 bytes ('A','M','O') are checked, per
            // UOReader. The 4th byte is read but ignored.
            if (data[0] != (byte)'A' || data[1] != (byte)'M' || data[2] != (byte)'O')
                return Array.Empty<EcAnimFrame>();

            int total       = BitConverter.ToInt32(data, 0x08);
            int colourCount = BitConverter.ToInt32(data, 0x18);
            int colourOff   = BitConverter.ToInt32(data, 0x1C);
            int frameCount  = BitConverter.ToInt32(data, 0x20);
            int frameOff    = BitConverter.ToInt32(data, 0x24);

            if (colourCount <= 0 || frameCount <= 0) return Array.Empty<EcAnimFrame>();
            if (colourOff < 0 || colourOff + colourCount * 4 > data.Length) return Array.Empty<EcAnimFrame>();
            if (frameOff < 0 || frameOff + frameCount * 16 > data.Length) return Array.Empty<EcAnimFrame>();

            // Palette as 0xAABBGGRR (XNA Color is RGBA little-endian).
            uint[] palette = new uint[colourCount];
            for (int i = 0; i < colourCount; i++)
            {
                int o = colourOff + i * 4;
                byte r = data[o];
                byte g = data[o + 1];
                byte b = data[o + 2];
                // data[o+3] is the body-specific alpha-flag, not a real
                // alpha channel — force opaque on solid writes.
                palette[i] = 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
            }

            var frames = new EcAnimFrame[frameCount];
            int[] pixelStarts = new int[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                int o = frameOff + i * 16;
                // ushort id     = BitConverter.ToUInt16(data, o);
                // ushort fidx   = BitConverter.ToUInt16(data, o + 2);
                short ix      = BitConverter.ToInt16(data, o + 4);
                short iy      = BitConverter.ToInt16(data, o + 6);
                short ex      = BitConverter.ToInt16(data, o + 8);
                short ey      = BitConverter.ToInt16(data, o + 10);
                int rel       = BitConverter.ToInt32(data, o + 12);

                int width = ex - ix;
                int height = ey - iy;
                if (width <= 0 || height <= 0)
                {
                    frames[i] = default;
                    pixelStarts[i] = total;
                    continue;
                }
                frames[i].InitX = ix;
                frames[i].InitY = iy;
                frames[i].Width = width;
                frames[i].Height = height;
                frames[i].CenterX = (short)(-ix);
                frames[i].CenterY = (short)(-(iy + height));
                pixelStarts[i] = frameOff + i * 16 + rel;
            }

            // Determine each frame's pixel-data end via the next distinct
            // start in sorted order; mirrors the Python reference.
            var sortedStarts = new SortedSet<int>(pixelStarts) { total };
            // Build a quick next-after lookup.
            var nextAfter = new Dictionary<int, int>();
            int? prev = null;
            foreach (int s in sortedStarts)
            {
                if (prev.HasValue) nextAfter[prev.Value] = s;
                prev = s;
            }

            for (int i = 0; i < frameCount; i++)
            {
                if (frames[i].Width <= 0) continue;
                int start = pixelStarts[i];
                if (!nextAfter.TryGetValue(start, out int end)) end = total;
                if (start < 0 || end > data.Length || start > end) continue;

                uint[] pixels = DecodeFrame(data, start, end, frames[i].Width, frames[i].Height, palette);
                if (pixels == null) continue;
                // AMOU sprites are ~1.5× the CC pixel pitch (body 400 idle
                // is 30×64, CC equivalent is ~20×42). Downsample 2/3 so the
                // EC frames display at the same world-pixel size as CC and
                // the CenterX/Y anchor math (in CC pixels) lines up.
                Downsample2of3(ref pixels, ref frames[i]);
                frames[i].Pixels = pixels;
            }

            return frames;
        }

        private static uint[] DecodeFrame(byte[] data, int start, int end, int width, int height, uint[] palette)
        {
            uint[] pixels = new uint[width * height];   // zero-init = transparent
            int off = start;
            int total = width * height;
            int pos = 0;

            while (pos < total && off < end)
            {
                byte b = data[off++];
                if (b < 128)
                {
                    pos += b;
                    continue;
                }

                int nSolid = b - 128;
                if (off >= end) break;
                byte b2 = data[off++];
                int hi = b2 >> 4;
                int lo = b2 & 0x0F;

                if (hi > 0)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    uint baseCol = palette[idx];
                    uint prior = pos < total ? pixels[pos] : 0u;
                    pixels[pos] = Blend(baseCol, prior, hi);
                    pos++;
                }

                for (int k = 0; k < nSolid && pos < total; k++)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    pixels[pos++] = palette[idx];
                }

                if (lo > 0 && pos < total)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    uint baseCol = palette[idx];
                    uint prior = pixels[pos];
                    pixels[pos] = Blend(baseCol, prior, lo);
                    pos++;
                }
            }

            return pixels;
        }

        // Nearest-neighbor 2/3 downsample of a frame's pixel buffer + bbox
        // metadata. New size = floor(old * 2 / 3). Center / Init coords scale
        // by the same factor so the anchor stays consistent post-scale.
        private static void Downsample2of3(ref uint[] pixels, ref EcAnimFrame f)
        {
            int srcW = f.Width, srcH = f.Height;
            int dstW = srcW * 2 / 3;
            int dstH = srcH * 2 / 3;
            if (dstW <= 0 || dstH <= 0 || (dstW == srcW && dstH == srcH))
                return;

            uint[] dst = new uint[dstW * dstH];
            for (int y = 0; y < dstH; y++)
            {
                int sy = y * srcH / dstH;
                int srcRow = sy * srcW;
                int dstRow = y * dstW;
                for (int x = 0; x < dstW; x++)
                {
                    int sx = x * srcW / dstW;
                    dst[dstRow + x] = pixels[srcRow + sx];
                }
            }
            pixels = dst;
            f.Width = dstW;
            f.Height = dstH;
            f.InitX = (short)(f.InitX * 2 / 3);
            f.InitY = (short)(f.InitY * 2 / 3);
            f.CenterX = (short)(-f.InitX);
            f.CenterY = (short)(-(f.InitY + dstH));
        }

        // 4-bit per-channel lerp: out = base*w/16 + prior*(16-w)/16  for RGB,
        // alpha forced to 255 on output. Matches UOReader's nibble blender.
        private static uint Blend(uint baseCol, uint prior, int w)
        {
            uint br = baseCol & 0xFF;
            uint bg = (baseCol >> 8) & 0xFF;
            uint bb = (baseCol >> 16) & 0xFF;
            uint pr = prior & 0xFF;
            uint pg = (prior >> 8) & 0xFF;
            uint pb = (prior >> 16) & 0xFF;
            int iw = 16 - w;
            uint r = (br * (uint)w + pr * (uint)iw) >> 4;
            uint g = (bg * (uint)w + pg * (uint)iw) >> 4;
            uint b = (bb * (uint)w + pb * (uint)iw) >> 4;
            return 0xFF000000u | (b << 16) | (g << 8) | r;
        }

        public void Dispose()
        {
            _cache.Clear();
            _missing.Clear();
        }
    }
}
