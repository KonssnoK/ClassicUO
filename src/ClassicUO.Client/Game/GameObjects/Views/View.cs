// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    enum ObjectHandlesStatus
    {
        NONE,
        OPEN,
        CLOSED,
        DISPLAYING
    }

    internal abstract partial class GameObject
    {
        public byte AlphaHue;
        public bool AllowedToDraw = true;
        public bool InChunkMesh;
        public int MeshSpriteIndex = -1;
        public ObjectHandlesStatus ObjectHandlesStatus;
        public Rectangle FrameInfo;
        protected bool IsFlipped;

        public abstract bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float CalculateDepthZ()
        {
            int x = X;
            int y = Y;
            int z = PriorityZ;

            // Offsets are in SCREEN coordinates
            if (Offset.X > 0 && Offset.Y < 0)
            {
                // North
            }
            else if (Offset.X > 0 && Offset.Y == 0)
            {
                // Northeast
                x++;
            }
            else if (Offset.X > 0 && Offset.Y > 0)
            {
                // East
                z += Math.Max(0, (int)Offset.Z);
                x++;
            }
            else if (Offset.X == 0 && Offset.Y > 0)
            {
                // Southeast
                x++;
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // South
                z += Math.Max(0, (int)Offset.Z);
                y++;
            }
            else if (Offset.X < 0 && Offset.Y == 0)
            {
                // Southwest
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // West
            }
            else if (Offset.X == 0 && Offset.Y < 0)
            {
                // Northwest
            }

            return (x + y) + (127 + z) * 0.01f;
        }

        public Rectangle GetOnScreenRectangle()
        {
            Rectangle prect = Rectangle.Empty;

            prect.X = (int)(RealScreenPosition.X - FrameInfo.X + 22 + Offset.X);
            prect.Y = (int)(RealScreenPosition.Y - FrameInfo.Y + 22 + (Offset.Y - Offset.Z));
            prect.Width = FrameInfo.Width;
            prect.Height = FrameInfo.Height;

            return prect;
        }

        public virtual bool TransparentTest(int z)
        {
            return false;
        }

        protected static void DrawStatic(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth,
            bool isWet = false
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // EC HD/legacy DDS fast path. Falls back to CC art on miss so
            // partial EC coverage doesn't break rendering.
            //
            // `graphic` here is the world-packet item id (0..0xFFFF). The EC
            // archives — like CC `art.mul` — index statics at `id + 0x4000`,
            // so we add the offset before the hash lookup.
            //
            // EC's DDS canvas is bigger than CC's TGA (e.g. 64x128 vs 44x89),
            // BUT the sprite content sits at the SAME pixel offset within
            // the canvas as in CC. So we anchor using CC's canvas dimensions
            // and draw the whole EC canvas — the two origins coincide.
            var ec = Client.Game.UO.EcArts;
            int ecArtIndex = graphic + 0x4000;
            if (artInfo.Texture != null
                && ec != null && ec.IsEnabled
                && ec.TryGet(ecArtIndex, out var ecArt))
            {
                int ax, ay;
                Rectangle src;
                Vector2 drawScale;
                if (ecArt.FromHd)
                {
                    // Align HD content's BOTTOM-RIGHT to CC content's
                    // bottom-right on screen so the figure stands on the
                    // same baseline as CC (pillars sit on their pedestals,
                    // etc.). HD content lives at HD canvas (0,0) for most
                    // tiles and naturally extends further when scaled.
                    const float HD_TO_CC = 1f / 1.5f;
                    var ccBox = Client.Game.UO.Arts.GetRealArtBounds((uint)graphic);
                    int ccCanvasW = artInfo.UV.Width;
                    int ccCanvasH = artInfo.UV.Height;
                    int contentBR_X = x - (ccCanvasW >> 1) + 22 + ccBox.X + ccBox.Width;
                    int contentBR_Y = y - ccCanvasH + 44 + ccBox.Y + ccBox.Height;
                    int dispW = (int)(ecArt.Source.Width  * HD_TO_CC);
                    int dispH = (int)(ecArt.Source.Height * HD_TO_CC);
                    ax = x - (contentBR_X - dispW);
                    ay = y - (contentBR_Y - dispH);
                    src = ecArt.Source;
                    drawScale = new Vector2(HD_TO_CC, HD_TO_CC);
                }
                else
                {
                    ax = (artInfo.UV.Width >> 1) - 22;
                    ay = artInfo.UV.Height - 44;
                    src = new Rectangle(0, 0, ecArt.Texture.Width, ecArt.Texture.Height);
                    drawScale = Vector2.One;
                }

                Vector3 ecHue = hue;
                if (ecArt.FromHd && !ec.HasHueMask(ecArtIndex))
                {
                    ecHue.Y = ShaderHueTranslator.SHADER_NONE;
                }
                batcher.Draw(
                    ecArt.Texture,
                    new Vector2(x - ax, y - ay),
                    src,
                    ecHue,
                    0f,
                    Vector2.Zero,
                    drawScale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
                return;
            }

            if (artInfo.Texture != null)
            {
                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.UV.Width >> 1) - 22);
                index.Height = (short)(artInfo.UV.Height - 44);

                x -= index.Width;
                y -= index.Height;

                var pos = new Vector2(x, y);
                var scale = Vector2.One;
                if (isWet)
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                }

                batcher.Draw(
                    artInfo.Texture,
                    pos,
                    artInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawGump(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth
        )
        {
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(graphic);

            if (gumpInfo.Texture != null)
            {
                batcher.Draw(
                    gumpInfo.Texture,
                    new Vector2(x, y),
                    gumpInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    1f,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawStaticRotated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            float angle,
            Vector3 hue,
            float depth
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.UV.Width >> 1) - 22);
                index.Height = (short)(artInfo.UV.Height - 44);

                batcher.Draw(
                    artInfo.Texture,
                    new Rectangle(
                        x - index.Width,
                        y - index.Height,
                        artInfo.UV.Width,
                        artInfo.UV.Height
                    ),
                    artInfo.UV,
                    hue,
                    angle,
                    Vector2.Zero,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawStaticAnimated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            bool shadow,
            float depth,
            bool isWet = false
        )
        {
            // Capture the original (pre-animation) item id for the EC lookup.
            // In EC, animated frames are stored INSIDE the base tile's record
            // (via SUB_9_4) — so looking up post-AnimOffset would land on
            // some unrelated EC tile and paint random art in place of (e.g.)
            // animated trees.
            ushort baseGraphic = graphic;

            ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);

            graphic = (ushort)(graphic + index.AnimOffset);

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // EC HD/legacy DDS fast path. EC's canvas is bigger than CC's TGA
            // but the sprite content sits at the same pixel offset within it,
            // so we anchor using CC's canvas dimensions and draw the whole
            // EC canvas. Falls back to CC art on miss.
            var ec = Client.Game.UO.EcArts;
            int ecArtIndex = baseGraphic + 0x4000;
            if (ec != null && ec.IsEnabled)
            {
                if (artInfo.Texture != null && ec.TryGet(ecArtIndex, out var ecArt))
                {
                    // Legacy: CC and legacy canvases share their (0,0) origin.
                    // HD: scale the alpha-trimmed bbox to fit CC's canvas
                    // dimensions, then bottom-center it on the cell foot.
                    int ax, ay;
                    Rectangle src;
                    Vector2 drawScale;
                    if (ecArt.FromHd)
                    {
                        const float HD_TO_CC = 1f / 1.5f;
                        var ccBox = Client.Game.UO.Arts.GetRealArtBounds((uint)baseGraphic);
                        int ccCanvasW = artInfo.UV.Width;
                        int ccCanvasH = artInfo.UV.Height;
                        int contentBR_X = x - (ccCanvasW >> 1) + 22 + ccBox.X + ccBox.Width;
                        int contentBR_Y = y - ccCanvasH + 44 + ccBox.Y + ccBox.Height;
                        int dispW = (int)(ecArt.Source.Width  * HD_TO_CC);
                        int dispH = (int)(ecArt.Source.Height * HD_TO_CC);
                        ax = x - (contentBR_X - dispW);
                        ay = y - (contentBR_Y - dispH);
                        src = ecArt.Source;
                        drawScale = new Vector2(HD_TO_CC, HD_TO_CC);
                    }
                    else
                    {
                        ax = (artInfo.UV.Width >> 1) - 22;
                        ay = artInfo.UV.Height - 44;
                        src = new Rectangle(0, 0, ecArt.Texture.Width, ecArt.Texture.Height);
                        drawScale = Vector2.One;
                    }
                    var pos = new Vector2(x - ax, y - ay);

                    if (shadow)
                    {
                        // Shadow uses the CC texture/UV so the silhouette is
                        // tight: EC's canvas has lots of transparent padding
                        // that would otherwise render as an oversized blob.
                        batcher.DrawShadow(artInfo.Texture, pos, artInfo.UV, false, depth + 0.25f);
                    }
                    Vector3 ecHue = hue;
                    if (ecArt.FromHd && !ec.HasHueMask(ecArtIndex))
                    {
                        ecHue.Y = ShaderHueTranslator.SHADER_NONE;
                    }
                    batcher.Draw(
                        ecArt.Texture,
                        pos,
                        src,
                        ecHue,
                        0f,
                        Vector2.Zero,
                        drawScale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );
                    return;
                }

                // Diagnostic mode: when EC has no replacement, draw nothing.
                // The user instantly sees which world statics actually have
                // EC sprites in this install.
                if (ec.DiagnosticMode) return;
            }

            if (artInfo.Texture != null)
            {
                index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.UV.Width >> 1) - 22);
                index.Height = (short)(artInfo.UV.Height - 44);

                x -= index.Width;
                y -= index.Height;

                Vector2 pos = new Vector2(x, y);

                if (shadow)
                {
                    batcher.DrawShadow(artInfo.Texture, pos, artInfo.UV, false, depth + 0.25f);
                }

                var scale = Vector2.One;
                if (isWet)
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.UV,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                }

                batcher.Draw(
                    artInfo.Texture,
                    pos,
                    artInfo.UV,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }
    }
}
