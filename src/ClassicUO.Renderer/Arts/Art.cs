using ClassicUO.Assets;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Buffers;

namespace ClassicUO.Renderer.Arts
{
    public sealed class Art
    {
        private readonly SpriteInfo[] _spriteInfos;
        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly Rectangle[] _realArtBounds;
        private readonly ArtLoader _artLoader;
        private readonly HuesLoader _huesLoader;

        public Art(ArtLoader artLoader, HuesLoader huesLoader, GraphicsDevice device)
        {
            _artLoader = artLoader;
            _huesLoader = huesLoader;
            int atlasSize = artLoader.HDArtAvailable ? 8192 : 4096;
            _atlas = new TextureAtlas(device, atlasSize, atlasSize, SurfaceFormat.Color);
            _spriteInfos = new SpriteInfo[_artLoader.File.Entries.Length];
            _realArtBounds = new Rectangle[_spriteInfos.Length];
        }

        public ref readonly SpriteInfo GetLand(uint idx)
            => ref Get((uint)(idx & ~0x4000));

        public ref readonly SpriteInfo GetArt(uint idx)
            => ref Get(idx + 0x4000);

        private ref readonly SpriteInfo Get(uint idx)
        {
            if (idx >= _spriteInfos.Length)
                return ref SpriteInfo.Empty;

            ref var spriteInfo = ref _spriteInfos[idx];

            if (spriteInfo.Texture == null)
            {
                var artInfo = _artLoader.GetArt(idx);

                if (artInfo.Pixels.IsEmpty && idx > 0)
                {
                    // Trying to load a texture that does not exist in the client MULs
                    // Degrading gracefully and only crash if not even the fallback ItemID exists
                    Log.Error(
                        $"Texture not found for sprite: idx: {idx}; itemid: {(idx > 0x4000 ? idx - 0x4000 : '-')}"
                    );
                    return ref Get(0); // ItemID of "UNUSED" placeholder
                }

                spriteInfo.Texture = _atlas.AddSprite(
                    artInfo.Pixels,
                    artInfo.Width,
                    artInfo.Height,
                    out spriteInfo.UV
                );
                spriteInfo.LogicalSize = new Point(artInfo.LogicalWidth, artInfo.LogicalHeight);

                if (idx > 0x4000)
                {
                    idx -= 0x4000;
                    _picker.Set(idx, artInfo.Width, artInfo.Height, artInfo.Pixels);

                    var pos1 = 0;
                    int minX = artInfo.Width,
                        minY = artInfo.Height,
                        maxX = 0,
                        maxY = 0;

                    for (int y = 0; y < artInfo.Height; ++y)
                    {
                        for (int x = 0; x < artInfo.Width; ++x)
                        {
                            if (artInfo.Pixels[pos1++] != 0)
                            {
                                minX = Math.Min(minX, x);
                                maxX = Math.Max(maxX, x);
                                minY = Math.Min(minY, y);
                                maxY = Math.Max(maxY, y);
                            }
                        }
                    }

                    _realArtBounds[idx] = new Rectangle(minX, minY, maxX - minX, maxY - minY);
                }
                
            }

            return ref spriteInfo;
        }

        public unsafe IntPtr CreateCursorSurfacePtr(
            int index,
            ushort customHue,
            out int hotX,
            out int hotY,
            float dpiScale
        )
        {
            hotX = hotY = 0;

            var artInfo = _artLoader.GetArt((uint)(index + 0x4000));

            if (artInfo.Pixels.IsEmpty)
            {
                return IntPtr.Zero;
            }

            int srcWidth = artInfo.Width;
            int srcHeight = artInfo.Height;
            int logicalWidth = artInfo.LogicalWidth > 0 ? artInfo.LogicalWidth : srcWidth;
            int logicalHeight = artInfo.LogicalHeight > 0 ? artInfo.LogicalHeight : srcHeight;

            // Make a copy of pixels to avoid modifying the original
            var rentedBuffer = ArrayPool<uint>.Shared.Rent(artInfo.Pixels.Length);
            try
            {
                var pixelsCopy = rentedBuffer.AsSpan(0, artInfo.Pixels.Length);
                artInfo.Pixels.CopyTo(pixelsCopy);

                // HD assets have an N-wide edge ring of upscaler-haloed pixels. Clear N=HDratio
                // rows/cols on each side so the cursor's outline isn't a thick dirty border.
                int hdRatio = srcWidth / logicalWidth;
                if (hdRatio < 1) hdRatio = 1;

                // Process the copy: find hotX/Y and clear marker pixels
                for (int y = 0; y < srcHeight; y++)
                {
                    for (int x = 0; x < srcWidth; x++)
                    {
                        int idx = y * srcWidth + x;
                        uint pixel = pixelsCopy[idx];

                        if (pixel == 0)
                            continue;

                        // Clear black marker pixels
                        if (pixel == 0xFF_00_00_00)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Check for green hotspot marker in first row/column
                        if (pixel == 0xFF_00_FF_00)
                        {
                            if (x == 0)
                                hotY = y;
                            if (y == 0)
                                hotX = x;
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Clear edge pixels (first/last hdRatio rows and columns)
                        if (x < hdRatio || y < hdRatio || x >= srcWidth - hdRatio || y >= srcHeight - hdRatio)
                        {
                            pixelsCopy[idx] = 0;
                            continue;
                        }

                        // Apply custom hue if needed
                        if (customHue > 0)
                        {
                            Color c = default;
                            c.PackedValue = pixel;
                            pixelsCopy[idx] = HuesHelper.Color16To32(
                                _huesLoader.GetColor16(
                                    HuesHelper.ColorToHue(c),
                                    customHue
                                )
                            ) | 0xFF_00_00_00;
                        }
                    }
                }

                // hotX/Y are in source-pixel coords; scale them down to logical-pixel space,
                // then up by dpiScale (matches the surface's final size below).
                hotX = (int)((hotX / (float)hdRatio) * dpiScale);
                hotY = (int)((hotY / (float)hdRatio) * dpiScale);

                // Create surface at HD size, then scale to (logical * dpi) so the OS cursor
                // matches what the rest of the UI considers "logical" pixel size.
                fixed (uint* ptr = pixelsCopy)
                {
                    SDL.SDL_Surface* surface = (SDL.SDL_Surface*)
                        SDL.SDL_CreateSurfaceFrom(
                            srcWidth,
                            srcHeight,
                            SDL.SDL_PixelFormat.SDL_PIXELFORMAT_ABGR8888,
                            (IntPtr)ptr,
                            4 * srcWidth);

                    int finalW = (int)(logicalWidth * dpiScale);
                    int finalH = (int)(logicalHeight * dpiScale);
                    if (finalW < 1) finalW = 1;
                    if (finalH < 1) finalH = 1;

                    if (finalW != srcWidth || finalH != srcHeight)
                    {
                        SDL.SDL_Surface* newSurface = (SDL.SDL_Surface*)SDL.SDL_ScaleSurface(
                            (nint)surface,
                            finalW,
                            finalH,
                            SDL.SDL_ScaleMode.SDL_SCALEMODE_NEAREST);

                        SDL.SDL_DestroySurface((nint)surface);
                        surface = newSurface;
                    }

                    return (IntPtr)surface;
                }
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(rentedBuffer);
            } 
        }

        public Rectangle GetRealArtBounds(uint idx) =>
            idx < 0 || idx >= _realArtBounds.Length
                ? Rectangle.Empty
                : _realArtBounds[idx];

        public bool PixelCheck(uint idx, int x, int y, int extraRange = 0) => _picker.Get(idx, x, y, extraRange);
    }
}
