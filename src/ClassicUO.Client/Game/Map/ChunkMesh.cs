// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// Tracks per-texture sprite counts and write cursors for bucket-insert building.
    /// Avoids dictionary overhead by using linear search (typically &lt; 20 unique textures per chunk).
    /// </summary>
    internal struct TextureBucketTracker
    {
        private Texture2D[] _textures;
        private int[] _counts;
        private int[] _cursors;
        private int _bucketCount;

        public TextureBucketTracker(int initialCapacity)
        {
            _textures = new Texture2D[initialCapacity];
            _counts = new int[initialCapacity];
            _cursors = new int[initialCapacity];
            _bucketCount = 0;
        }

        public void Clear()
        {
            if (_bucketCount > 0)
                Array.Clear(_textures, 0, _bucketCount);
            _bucketCount = 0;
        }

        /// <summary>
        /// Registers a texture during pass 1 (counting phase).
        /// </summary>
        public void Count(Texture2D texture)
        {
            for (int i = 0; i < _bucketCount; i++)
            {
                if (_textures[i] == texture)
                {
                    _counts[i]++;
                    return;
                }
            }

            if (_bucketCount >= _textures.Length)
            {
                int newSize = _textures.Length * 2;
                Array.Resize(ref _textures, newSize);
                Array.Resize(ref _counts, newSize);
                Array.Resize(ref _cursors, newSize);
            }

            _textures[_bucketCount] = texture;
            _counts[_bucketCount] = 1;
            _bucketCount++;
        }

        /// <summary>
        /// Computes prefix-sum offsets from the counts. Returns total sprite count.
        /// After this, GetNextIndex() can be called during pass 2.
        /// </summary>
        public int ComputeOffsets()
        {
            int offset = 0;
            for (int i = 0; i < _bucketCount; i++)
            {
                _cursors[i] = offset;
                offset += _counts[i];
            }
            return offset;
        }

        /// <summary>
        /// Returns the next write index for this texture and advances its cursor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetNextIndex(Texture2D texture)
        {
            for (int i = 0; i < _bucketCount; i++)
            {
                if (_textures[i] == texture)
                    return _cursors[i]++;
            }

            // Should never happen if pass 1 counted correctly
            return -1;
        }
    }

    internal sealed class ChunkMesh
    {
        public readonly MeshLayer Land = new();
        public readonly MeshLayer Statics = new();

        public bool IsDirty = true;

        private bool _animatedWaterEffect;
        private TextureBucketTracker _landBuckets = new(16);
        private TextureBucketTracker _staticsBuckets = new(32);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkDirtyIfNeeded(GameObject obj)
        {
            if (!IsDirty && obj is GameObjects.Land or Static or Multi)
                IsDirty = true;
        }

        public void Build(Chunk chunk, World world, GraphicsDevice graphicsDevice)
        {
            Land.Reset();
            Statics.Reset();
            IsDirty = false;

            var profile = ProfileManager.CurrentProfile;
            if (profile == null)
                return;

            _animatedWaterEffect = profile.AnimatedWaterEffect;

            // Pass 1: count sprites per texture
            _landBuckets.Clear();
            _staticsBuckets.Clear();

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        obj.InChunkMesh = false;
                        obj.MeshSpriteIndex = -1;

                        switch (obj)
                        {
                            case GameObjects.Land land:
                                CountLand(land);
                                break;

                            case Static staticObj:
                                CountStatic(staticObj);
                                break;

                            case Multi multi:
                                CountMulti(multi);
                                break;
                        }
                    }
                }
            }

            int landTotal = _landBuckets.ComputeOffsets();
            int staticsTotal = _staticsBuckets.ComputeOffsets();

            Land.EnsureCapacity(landTotal);
            Land.Count = landTotal;
            Statics.EnsureCapacity(staticsTotal);
            Statics.Count = staticsTotal;

            // Pass 2: write sprites at their final sorted positions
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        switch (obj)
                        {
                            case GameObjects.Land land:
                                TryAddLand(land);
                                break;

                            case Static staticObj:
                                TryAddStatic(staticObj);
                                break;

                            case Multi multi:
                                TryAddMulti(multi);
                                break;
                        }
                    }
                }
            }

            Land.UploadVertexBuffer(graphicsDevice);
            Statics.UploadVertexBuffer(graphicsDevice);
        }

        public void Clear()
        {
            Land.Reset();
            Land.Dispose();
            Statics.Reset();
            Statics.Dispose();
            IsDirty = true;
        }

        /// <summary>
        /// Resets mesh state for pool reuse. Keeps GPU buffers and arrays allocated.
        /// </summary>
        public void SoftClear()
        {
            Land.SoftReset();
            Statics.SoftReset();
            _landBuckets.Clear();
            _staticsBuckets.Clear();
            IsDirty = true;
        }

        #region Pass 1: Texture counting

        private void CountLand(GameObjects.Land land)
        {
            if (!land.AllowedToDraw || land.IsDestroyed)
                return;

            if (_animatedWaterEffect && land.TileData.IsWet)
                return;

            Texture2D texture;
            if (land.IsStretched)
            {
                ref readonly var texmapInfo = ref Client.Game.UO.Texmaps.GetTexmap(
                    Client.Game.UO.FileManager.TileData.LandData[land.Graphic].TexID
                );

                if (texmapInfo.Texture != null)
                    texture = texmapInfo.Texture;
                else
                {
                    ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(land.Graphic);
                    if (artInfo.Texture == null)
                        return;
                    texture = artInfo.Texture;
                }
            }
            else
            {
                ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(land.Graphic);
                if (artInfo.Texture == null)
                    return;
                texture = artInfo.Texture;
            }

            _landBuckets.Count(texture);
        }

        private void CountStatic(Static staticObj)
        {
            if (!staticObj.AllowedToDraw || staticObj.IsDestroyed)
                return;

            CountStaticLike(ref staticObj.ItemData, staticObj.Graphic);
        }

        private void CountMulti(Multi multi)
        {
            if (!multi.AllowedToDraw || multi.IsDestroyed)
                return;

            if (multi.State != 0)
                return;

            CountStaticLike(ref multi.ItemData, multi.Graphic);
        }

        private void CountStaticLike(ref StaticTiles itemData, ushort graphic)
        {
            if (IsStaticExcludedFromMesh(graphic, ref itemData))
                return;

            var ec = Client.Game.UO.EcArts;
            if (ec != null && ec.IsEnabled)
            {
                // Surface-tile bypass must match Pass 2 (TryAddStaticLike)
                // exactly — Pass 1 sizes the buckets, Pass 2 fills them.
                // If Pass 1 counts EC art but Pass 2 skips it (or vice
                // versa) the GetNextIndex stream goes out of sync and
                // WriteQuadAt crashes / writes to wrong slots.
                // Iso-rotation flag = bit 34 of FlagsEc in the tileart
                // record (UOReader labels it "Unused1" in its EC FLAGS
                // line). It's an EC-engine-only flag — CC's tiledata
                // doesn't expose it. Set on tiles whose HD master is
                // axis-aligned and needs iso-projection (carpets, multi-
                // texture composites, slate roofs). Walls, statues,
                // signposts don't have it. We fall back to CC's pre-iso-
                // projected art for these tiles.
                const ulong EC_FLAG_NEEDS_ISO = 1UL << 34;
                bool needsIsoBypass = ec.Mode == Renderer.Arts.EcArtMode.UopKR
                    && Client.Game.UO.FileManager.EcTileArt.TryGet(
                           graphic + 0x4000, out var ___ecMeta)
                    && ___ecMeta != null
                    && (___ecMeta.FlagsEc & EC_FLAG_NEEDS_ISO) != 0;
                if (!needsIsoBypass)
                {
                if (ec.IsAbsorbedByHdSibling(graphic + 0x4000))
                    return;
                if (ec.TryGet(graphic + 0x4000, out var ecArt))
                {
                    // Outline-debug mode: skip BOTH counting and vertex-write
                    // so EC-replaced tiles vanish, making them obvious.
                    if (ec.OutlineMode) return;
                    // Route to EC bucket — each EC tile's DDS is its own
                    // Texture2D so this becomes one draw call per unique tile.
                    _staticsBuckets.Count(ecArt.Texture);
                    return;
                }
                if (ec.DiagnosticMode)
                    return;
                }
            }

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);
            if (artInfo.Texture == null)
                return;

            _staticsBuckets.Count(artInfo.Texture);
        }

        #endregion

        #region Pass 2: Vertex building

        private void TryAddLand(GameObjects.Land land)
        {
            if (!land.AllowedToDraw || land.IsDestroyed)
                return;

            if (_animatedWaterEffect && land.TileData.IsWet)
                return;

            ushort hue = land.Hue;
            Vector3 hueVec;
            if (hue != 0)
            {
                hueVec.X = hue - 1;
                hueVec.Y = land.IsStretched
                    ? ShaderHueTranslator.SHADER_LAND_HUED
                    : ShaderHueTranslator.SHADER_HUED;
            }
            else
            {
                hueVec.X = 0;
                hueVec.Y = land.IsStretched
                    ? ShaderHueTranslator.SHADER_LAND
                    : ShaderHueTranslator.SHADER_NONE;
            }
            hueVec.Z = 1f;

            float depth = land.CalculateDepthZ() + 0.5f;
            int baseX = (land.X - land.Y) * 22 - 22;
            int baseY = (land.X + land.Y) * 22 - (land.Z << 2) - 22;

            if (land.IsStretched)
            {
                ref readonly var texmapInfo = ref Client.Game.UO.Texmaps.GetTexmap(
                    Client.Game.UO.FileManager.TileData.LandData[land.Graphic].TexID
                );

                if (texmapInfo.Texture != null)
                {
                    int idx = _landBuckets.GetNextIndex(texmapInfo.Texture);
                    land.MeshSpriteIndex = idx;
                    WriteStretchedLand(
                        idx,
                        texmapInfo.Texture,
                        texmapInfo.UV,
                        baseX,
                        baseY + (land.Z << 2),
                        ref land.YOffsets,
                        ref land.NormalTop,
                        ref land.NormalRight,
                        ref land.NormalLeft,
                        ref land.NormalBottom,
                        hueVec,
                        depth
                    );
                    land.InChunkMesh = true;
                }
                else
                {
                    ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(land.Graphic);
                    if (artInfo.Texture != null)
                    {
                        int idx = _landBuckets.GetNextIndex(artInfo.Texture);
                        land.MeshSpriteIndex = idx;
                        Land.WriteQuadAt(idx, artInfo.Texture, artInfo.UV,
                            baseX, baseY + (land.Z << 2), hueVec, depth);
                        land.InChunkMesh = true;
                    }
                }
            }
            else
            {
                ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(land.Graphic);
                if (artInfo.Texture != null)
                {
                    int idx = _landBuckets.GetNextIndex(artInfo.Texture);
                    land.MeshSpriteIndex = idx;
                    Land.WriteQuadAt(idx, artInfo.Texture, artInfo.UV, baseX, baseY, hueVec, depth);
                    land.InChunkMesh = true;
                }
            }
        }

        /// <summary>
        /// Checks if a static/multi should be excluded from the chunk mesh.
        /// </summary>
        private static bool IsStaticExcludedFromMesh(ushort graphic, ref StaticTiles itemData)
        {
            if (itemData.IsInternal)
                return true;

            if (itemData.IsAnimated)
                return true;

            if (itemData.IsFoliage)
                return true;

            if (StaticFilters.IsTree(graphic, out _))
                return true;

            if (StaticFilters.IsRock(graphic))
                return true;

            return false;
        }

        private void TryAddStatic(Static staticObj)
        {
            if (!staticObj.AllowedToDraw || staticObj.IsDestroyed)
                return;

            TryAddStaticLike(staticObj, ref staticObj.ItemData, staticObj.Graphic, staticObj.Hue);
        }

        private void TryAddMulti(Multi multi)
        {
            if (!multi.AllowedToDraw || multi.IsDestroyed)
                return;

            if (multi.State != 0)
                return;

            TryAddStaticLike(multi, ref multi.ItemData, multi.Graphic, multi.Hue);
        }

        private void TryAddStaticLike(GameObject obj, ref StaticTiles itemData, ushort graphic, ushort hue)
        {
            if (IsStaticExcludedFromMesh(graphic, ref itemData))
                return;

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, itemData.IsPartialHue, 1f);

            float depth = obj.CalculateDepthZ() + 0.5f;
            int baseX = (obj.X - obj.Y) * 22 - 22;
            int baseY = (obj.X + obj.Y) * 22 - (obj.Z << 2) - 22;

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);
            if (artInfo.Texture == null)
                return;

            // EC fast path: per-tile DDS available. Anchor with CC's canvas
            // dimensions (EC just expanded the canvas; sprite content sits at
            // the same offset within it). CountStaticLike mirrors this branch.
            // EC textures are pre-colored, so we suppress partial-hue tinting
            // (which would double-tint things like leaves on trees).
            var ec = Client.Game.UO.EcArts;
            if (ec != null && ec.IsEnabled)
            {
                // Surface tiles (carpets, water, floors, slate roofs) are
                // pre-iso-projected in CC art (44×44 diamonds) but stored
                // as axis-aligned textures in EC's HD master. Rendering
                // the HD master as a normal sprite gives a flat square
                // instead of the iso diamond. Until the chunked-mesh
                // terrain renderer is wired in (which is what EC uses
                // for these tiles), fall back to CC art for the Surface
                // flag in KR mode — CC's pre-projected diamond renders
                // correctly. UopEC mode already works because the legacy
                // DDS is itself pre-projected.
                // Must match Pass 1 (CountStaticLike) exactly — using a
                // different predicate here drifts the bucket index stream
                // and crashes WriteQuadAt. Bit 34 of FlagsEc in the
                // tileart record (= UOReader's "Unused1") flags tiles
                // whose HD master is axis-aligned and needs iso-projection.
                const ulong EC_FLAG_NEEDS_ISO_2 = 1UL << 34;
                bool needsIsoBypass2 = ec.Mode == Renderer.Arts.EcArtMode.UopKR
                    && Client.Game.UO.FileManager.EcTileArt.TryGet(
                           graphic + 0x4000, out var ___ecMeta2)
                    && ___ecMeta2 != null
                    && (___ecMeta2.FlagsEc & EC_FLAG_NEEDS_ISO_2) != 0;
                if (needsIsoBypass2)
                {
                    // fall through to CC path below
                }
                else
                {
                if (ec.IsAbsorbedByHdSibling(graphic + 0x4000))
                    return;
                if (ec.TryGet(graphic + 0x4000, out var ecArt))
                {
                    int ax, ay;
                    Rectangle src;
                    float scaleX, scaleY;
                    if (ecArt.FromHd)
                    {
                        // KR HD EcImage-crop path. Horizontal anchor uses
                        // CC-bbox alignment alone: where CC's own art draws
                        // its content within the canvas. EC's dx happens
                        // to approximate this for W walls (positive shift)
                        // but is wrong for N walls (CC content is left-of-
                        // center but dx is near zero). Using CC-bbox as the
                        // single source of truth pulls EC content to CC's
                        // exact horizontal position, regardless of facing.
                        //
                        // Vertical anchor still uses dy via ecArt.AnchorY —
                        // dy IS the right signal for elevation (signposts
                        // with dy=-110 hang correctly).
                        int dispW = (int)(ecArt.Source.Width  * ecArt.Scale.X);
                        int dispH = (int)(ecArt.Source.Height * ecArt.Scale.Y);
                        var ccBox = Client.Game.UO.Arts.GetRealArtBounds((uint)graphic);
                        int ccBboxOffsetX = ccBox.X + (ccBox.Width >> 1) - (artInfo.UV.Width >> 1);
                        ax = (dispW >> 1) - 22 - ccBboxOffsetX;
                        ay = dispH       - 44 - ecArt.AnchorY;
                        src = ecArt.Source;
                        scaleX = ecArt.Scale.X;
                        scaleY = ecArt.Scale.Y;
                        // HD textures are typically already color-baked
                        // (per-pixel R != G != B). Applying CC's hue
                        // shader on top tints based on R only, washing
                        // out the natural color. When no mask is shipped
                        // (EC's HAS_HUEMASK_TEX == 0 path), the HD
                        // texture is meant to render as-is — no runtime
                        // tinting.
                        if (!Client.Game.UO.EcArts.HasHueMask(graphic + 0x4000))
                        {
                            hueVec.Y = ShaderHueTranslator.SHADER_NONE;
                        }
                    }
                    else
                    {
                        // Legacy DDS path (UopEC mode OR KR-mode fallback
                        // when the tile has no own HD master). Uses CC's
                        // canvas anchor — the EC DDS is POT-padded with
                        // content at the top-left, so CC's standard math
                        // (W/2-22, H-44) lands the content where CC would.
                        // anchorX/anchorY are 0 in UopEC mode (the flat 2D
                        // look) and non-zero in KR-fallback mode (carries
                        // the EcImage dx/dy so the legacy DDS sits where
                        // the HD master would have been placed).
                        ax = (artInfo.UV.Width  >> 1) - 22 - ecArt.AnchorX;
                        ay =  artInfo.UV.Height       - 44 - ecArt.AnchorY;
                        src = new Rectangle(0, 0, ecArt.Texture.Width, ecArt.Texture.Height);
                        scaleX = scaleY = 1f;
                        // KR-mode legacy fallback: bypass hue tinting so the
                        // noise-composited legacy DDS isn't re-shaded by the
                        // partial-hue path — keeps colour parity with the
                        // surrounding HD tiles (which already do this).
                        if (ec.Mode == Renderer.Arts.EcArtMode.UopKR)
                            hueVec.Y = ShaderHueTranslator.SHADER_NONE;
                    }
                    int ecPosX = baseX - ax;
                    int ecPosY = baseY - ay;

                    // Debug outline: when on, skip rendering EC statics
                    // entirely so EC-replaced tiles appear as gaps.
                    if (Client.Game.UO.EcArts.OutlineMode) return;

                    int ecIdx = _staticsBuckets.GetNextIndex(ecArt.Texture);
                    obj.MeshSpriteIndex = ecIdx;
                    Statics.WriteQuadAt(ecIdx, ecArt.Texture, src, ecPosX, ecPosY, hueVec, depth,
                                        scaleX: scaleX, scaleY: scaleY);
                    obj.InChunkMesh = true;
                    return;
                }
                if (ec.DiagnosticMode)
                    return;
                }   // close 'else' for Surface-tile bypass
            }

            ref var artIndex = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
            artIndex.Width = (short)((artInfo.UV.Width >> 1) - 22);
            artIndex.Height = (short)(artInfo.UV.Height - 44);

            int posX = baseX - artIndex.Width;
            int posY = baseY - artIndex.Height;

            int idx = _staticsBuckets.GetNextIndex(artInfo.Texture);
            obj.MeshSpriteIndex = idx;
            Statics.WriteQuadAt(idx, artInfo.Texture, artInfo.UV, posX, posY, hueVec, depth);
            obj.InChunkMesh = true;
        }

        private void WriteStretchedLand(
            int index,
            Texture2D texture, Rectangle sourceRect,
            int posX, int posY,
            ref YOffsets yOffsets,
            ref Vector3 normalTop, ref Vector3 normalRight,
            ref Vector3 normalLeft, ref Vector3 normalBottom,
            Vector3 hue, float depth)
        {
            ref var vertex = ref Land.Vertices[index];

            // Half-pixel inset to prevent texture bleeding at tile seams
            float sourceX = (sourceRect.X + 0.5f) / (float)texture.Width;
            float sourceY = (sourceRect.Y + 0.5f) / (float)texture.Height;
            float sourceW = (sourceRect.Width - 1f) / (float)texture.Width;
            float sourceH = (sourceRect.Height - 1f) / (float)texture.Height;

            vertex.TextureCoordinate0.X = sourceX;
            vertex.TextureCoordinate0.Y = sourceY;
            vertex.TextureCoordinate0.Z = 0;
            vertex.TextureCoordinate1.X = sourceW + sourceX;
            vertex.TextureCoordinate1.Y = sourceY;
            vertex.TextureCoordinate1.Z = 0;
            vertex.TextureCoordinate2.X = sourceX;
            vertex.TextureCoordinate2.Y = sourceH + sourceY;
            vertex.TextureCoordinate2.Z = 0;
            vertex.TextureCoordinate3.X = sourceW + sourceX;
            vertex.TextureCoordinate3.Y = sourceH + sourceY;
            vertex.TextureCoordinate3.Z = 0;

            vertex.Normal0 = normalTop;
            vertex.Normal1 = normalRight;
            vertex.Normal2 = normalLeft;
            vertex.Normal3 = normalBottom;

            vertex.Position0.X = posX + 22;
            vertex.Position0.Y = posY - yOffsets.Top;
            vertex.Position1.X = posX + 44;
            vertex.Position1.Y = posY + (22 - yOffsets.Right);
            vertex.Position2.X = posX;
            vertex.Position2.Y = posY + (22 - yOffsets.Left);
            vertex.Position3.X = posX + 22;
            vertex.Position3.Y = posY + (44 - yOffsets.Bottom);

            vertex.Position0.Z = vertex.Position1.Z = vertex.Position2.Z = vertex.Position3.Z = depth;

            vertex.Hue0 = vertex.Hue1 = vertex.Hue2 = vertex.Hue3 = hue;

            Land.Textures[index] = texture;
        }

        #endregion
    }
}
