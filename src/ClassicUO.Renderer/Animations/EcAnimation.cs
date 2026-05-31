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
            // Global (body-wide) bbox at 0x10..0x17 — defines the canvas that
            // each frame's content sits within. Per UOReader, each frame's
            // pixel content is placed at (frame.InitX - main.InitX,
            // frame.InitY - main.InitY) inside this canvas, so the body
            // anchor is stable across frames. Using per-frame InitX directly
            // (without referring to main) produces visible vibration.
            short mainInitX = BitConverter.ToInt16(data, 0x10);
            short mainInitY = BitConverter.ToInt16(data, 0x12);
            short mainEndX  = BitConverter.ToInt16(data, 0x14);
            short mainEndY  = BitConverter.ToInt16(data, 0x16);
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

                uint[] framePixels = DecodeFrame(data, start, end, frames[i].Width, frames[i].Height, palette);
                if (framePixels == null) continue;

                // Place this frame's content inside the body-wide canvas
                // at (frame.InitX - main.InitX, frame.InitY - main.InitY).
                // The result has a stable size (mainCanvas) across all
                // frames; CenterX/Y reference the main bbox so the body
                // anchor doesn't shift between frames (kills vibration).
                int mainCanvasW = mainEndX - mainInitX;
                int mainCanvasH = mainEndY - mainInitY;
                if (mainCanvasW <= 0 || mainCanvasH <= 0)
                {
                    // Degenerate main bbox — fall back to frame-local canvas.
                    frames[i].Pixels = framePixels;
                    frames[i].CenterX = (short)(-frames[i].InitX);
                    frames[i].CenterY = (short)(-(frames[i].InitY + frames[i].Height));
                    continue;
                }
                int fx = frames[i].InitX - mainInitX;
                int fy = frames[i].InitY - mainInitY;
                uint[] canvas = new uint[mainCanvasW * mainCanvasH];
                int copyW = System.Math.Min(frames[i].Width,  mainCanvasW - fx);
                int copyH = System.Math.Min(frames[i].Height, mainCanvasH - fy);
                for (int row = 0; row < copyH; row++)
                {
                    int dstRow = (fy + row) * mainCanvasW + fx;
                    int srcRow = row * frames[i].Width;
                    System.Array.Copy(framePixels, srcRow, canvas, dstRow, copyW);
                }
                frames[i].Pixels = canvas;
                frames[i].Width  = mainCanvasW;
                frames[i].Height = mainCanvasH;
                // CC anchor convention: screen_x = pos - CenterX,
                //                       screen_y = pos - (H + CenterY).
                // Body anchor at body-local (mainCenterX, mainEndY) so the
                // body's bottom-center sits on the world cell.
                // With every frame sharing the same canvas:
                //   body anchor in body-local = (mainCenterX, mainEndY)
                //   body anchor in canvas     = (mainCanvasW / 2, mainCanvasH)
                // → CC bottom-center anchor lands exactly on the body's
                //   natural foot/anchor point, regardless of frame.
                frames[i].CenterX = (short)(mainCanvasW / 2);
                frames[i].CenterY = 0;
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

                // AA edge pixels: encode the blend weight as the ALPHA
                // channel so they composite correctly against whatever
                // background the sprite sits over at draw time.
                // UOReader pre-blends against its preview canvas — that
                // produces wrong colors when the sprite renders over the
                // game world. Partial-alpha lets the GPU blend live.
                if (hi > 0)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    pixels[pos++] = WithAlpha(palette[idx], hi);
                }

                for (int k = 0; k < nSolid && pos < total; k++)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    pixels[pos++] = palette[idx];   // already alpha = 255
                }

                if (lo > 0 && pos < total)
                {
                    if (off >= end) break;
                    byte idx = data[off++];
                    pixels[pos++] = WithAlpha(palette[idx], lo);
                }
            }

            return pixels;
        }

        // Set alpha to (w * 255 / 16) — maps the 4-bit AA weight to a
        // proper alpha channel; w=15 → 239, w=8 → 127, w=1 → 15.
        // Leaves RGB intact so the GPU can blend against whatever's below.
        private static uint WithAlpha(uint rgba, int w)
        {
            uint a = (uint)((w * 255) / 16);
            return (rgba & 0x00FFFFFFu) | (a << 24);
        }

        public void Dispose()
        {
            _cache.Clear();
            _missing.Clear();
        }
    }
}
