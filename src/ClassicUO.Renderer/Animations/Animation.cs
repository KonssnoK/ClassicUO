using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Animations
{
    public sealed class Animations
    {
        const int MAX_ANIMATIONS_DATA_INDEX_COUNT = 2048;

        private readonly TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly AnimationsLoader _animationLoader;
        private IndexAnimation[] _dataIndex = new IndexAnimation[MAX_ANIMATIONS_DATA_INDEX_COUNT];

        private AnimationDirection[][][] _cache;

        /// <summary>
        /// Optional EC animation override. When set and its UseEc is true,
        /// GetAnimationFrames substitutes AMOU-decoded frames in place of
        /// CC frames whenever EC has the (body, action). Falls back to CC
        /// when EC has no matching entry.
        /// </summary>
        public EcAnimation Ec { get; set; }

        public Animations(AnimationsLoader animationLoader, GraphicsDevice device)
        {
            _animationLoader = animationLoader;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
        }

        /// <summary>
        /// Invalidate all cached animation directions — call this when
        /// switching animation source so the next draw rebuilds frames
        /// from the new source.
        ///
        /// Frames live on IndexAnimation.Groups[action].Direction[dir]
        /// (and UopGroups for UOP-flagged bodies); resetting FrameCount=0
        /// + SpriteInfos=null forces GetAnimationFrames to re-enter the
        /// decode branch on the next call.
        /// </summary>
        public void InvalidateCache()
        {
            if (_dataIndex == null) return;
            for (int b = 0; b < _dataIndex.Length; b++)
            {
                var idx = _dataIndex[b];
                if (idx == null) continue;
                ResetGroups(idx.Groups);
                ResetGroups(idx.UopGroups);
            }
        }

        private static void ResetGroups(AnimationGroup[] groups)
        {
            if (groups == null) return;
            for (int a = 0; a < groups.Length; a++)
            {
                var g = groups[a];
                if (g == null || g.Direction == null) continue;
                for (int d = 0; d < g.Direction.Length; d++)
                {
                    g.Direction[d].FrameCount = 0;
                    g.Direction[d].SpriteInfos = null;
                }
            }
        }


        private ref AnimationDirection GetSprite(int body, int action, int dir)
        {
            if (_cache == null)
                _cache = new AnimationDirection[Math.Max(body, MAX_ANIMATIONS_DATA_INDEX_COUNT)][][];

            if (body >= _cache.Length)
                Array.Resize(ref _cache, body);

            if (_cache[body] == null)
                _cache[body] = new AnimationDirection[AnimationsLoader.MAX_ACTIONS][];

            if (_cache[body][action] == null)
                _cache[body][action] = new AnimationDirection[AnimationsLoader.MAX_DIRECTIONS];

            return ref _cache[body][action][dir];
        }

        public int MaxAnimationCount => _dataIndex.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationGroupsType GetAnimType(ushort graphic) => graphic < _dataIndex.Length ? _dataIndex[graphic]?.Type ?? 0 : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationFlags GetAnimFlags(ushort graphic) => graphic < _dataIndex.Length ? _dataIndex[graphic]?.Flags ?? 0 : 0;

        public bool PixelCheck(
            ushort animID,
            byte group,
            byte direction,
            bool uop,
            int frame,
            int x,
            int y
        )
        {
            ConvertBodyIfNeeded(ref animID);

            if (uop)
            {
                _animationLoader.ReplaceUopGroup(animID, ref group);
            }

            uint packed32 = (uint)((group | (direction << 8) | ((uop ? 0x01 : 0x00) << 16)));
            uint packed32_2 = (uint)((animID | (frame << 16)));
            ulong packed = (packed32_2 | ((ulong)packed32 << 32));

            return _picker.Get(packed, x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetAnimDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        public void GetAnimationDimensions(
            byte animIndex,
            ushort graphic,
            byte dir,
            byte animGroup,
            bool ismounted,
            byte frameIndex,
            out int centerX,
            out int centerY,
            out int width,
            out int height
        )
        {
            dir &= 0x7F;
            bool mirror = false;
            GetAnimDirection(ref dir, ref mirror);

            if (frameIndex == 0xFF)
            {
                frameIndex = (byte)animIndex;
            }

            var frames = GetAnimationFrames(graphic, animGroup, dir, out _, out _, false);

            if (!frames.IsEmpty && frames[frameIndex].Texture != null)
            {
                centerX = frames[frameIndex].Center.X;
                centerY = frames[frameIndex].Center.Y;
                width = frames[frameIndex].UV.Width;
                height = frames[frameIndex].UV.Height;
                return;
            }

            centerX = 0;
            centerY = 0;
            width = 0;
            height = ismounted ? 100 : 60;
        }

        private IndexAnimation GetIndexAnim(ushort id, ref ushort hue, bool isCorpse)
        {
            if (id >= ushort.MaxValue)
                return null;

            if (id >= _dataIndex.Length)
                Array.Resize(ref _dataIndex, id + 1);

            ref var index = ref _dataIndex[id];
            do
            {
                if (index == null)
                {
                    index = new IndexAnimation();
                    var indices = _animationLoader.GetIndices
                    (
                        _animationLoader.FileManager.Version,
                        id,
                        ref hue,
                        ref index.Flags,
                        out index.FileIndex,
                        out index.Type
                    );

                    if (!indices.IsEmpty)
                    {
                        if ((index.Flags & AnimationFlags.UseUopAnimation) != 0)
                        {
                            index.UopGroups = new AnimationGroupUop[indices.Length];
                            for (int i = 0; i < index.UopGroups.Length; i++)
                            {
                                index.UopGroups[i] = new AnimationGroupUop();
                                index.UopGroups[i].FileIndex = index.FileIndex;
                                index.UopGroups[i].DecompressedLength = indices[i].UncompressedSize;
                                index.UopGroups[i].CompressedLength = indices[i].Size;
                                index.UopGroups[i].Offset = indices[i].Position;
                                index.UopGroups[i].CompressionType = indices[i].CompressionType;
                            }

                            break;
                        }

                        index.Groups = new AnimationGroup[indices.Length / AnimationsLoader.MAX_DIRECTIONS];
                        for (int i = 0; i < index.Groups.Length; i++)
                        {
                            index.Groups[i] = new AnimationGroup();

                            for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
                            {
                                ref readonly var animIdx = ref indices[i * AnimationsLoader.MAX_DIRECTIONS + d];
                                index.Groups[i].Direction[d].Address = animIdx.Position;
                                index.Groups[i].Direction[d].Size = /*index.FileIndex > 0 ? Math.Max(1, animIdx.Size) :*/ animIdx.Size;
                            }
                        }

                        //if (index.FileIndex == 0)
                        //{
                        //    var replaced = isCorpse ? _animationLoader.ReplaceCorpse(ref id, ref hue) : _animationLoader.ReplaceBody(ref id, ref hue);
                        //    if (replaced)
                        //    {
                        //        if (id >= _dataIndex.Length)
                        //        {
                        //            Array.Resize(ref _dataIndex, id + 1);
                        //        }

                        //        index = ref _dataIndex[id];
                        //    }
                        //}
                    }
                }
            } while (index == null);

            return index;
        }

        public Span<SpriteInfo> GetAnimationFrames(
            ushort id,
            byte action,
            byte dir,
            out ushort hue,
            out bool useUOP,
            bool isEquip = false,
            bool isCorpse = false,
            bool forceUOP = false
        )
        {
            hue = 0;
            useUOP = false;

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS)
            {
                return Span<SpriteInfo>.Empty;
            }

            var index = GetIndexAnim(id, ref hue, isCorpse);
            if (index == null)
                return Span<SpriteInfo>.Empty;

            useUOP = (index.Flags & AnimationFlags.UseUopAnimation) != 0;

            while (!useUOP && index.FileIndex == 0)
            {
                var replaced = isCorpse ? _animationLoader.ReplaceCorpse(ref id, ref hue) : _animationLoader.ReplaceBody(ref id, ref hue);
                if (!replaced)
                    break;

                index = GetIndexAnim(id, ref hue, isCorpse);
                if (index == null)
                    return Span<SpriteInfo>.Empty;
            }


            index.Hue = hue;

            if (useUOP)
            {
                _animationLoader.ReplaceUopGroup(id, ref action);
            }

            // When we are searching for an equipment item we must ignore any other animation which is not equipment
            var currentAnimType = GetAnimType(id);
            if (isEquip && currentAnimType != AnimationGroupsType.Equipment && currentAnimType != AnimationGroupsType.Human)
            {
                return Span<SpriteInfo>.Empty;
            }

            // NOTE:
            // for UOP: we don't call the method index.GetUopGroup(ref x) because the action has been already changed by the method ReplaceAnimationValues
            AnimationGroup groupObj = null;
            if (useUOP)
            {
                if (index.UopGroups == null || action >= index.UopGroups.Length)
                    return Span<SpriteInfo>.Empty;
                groupObj = index.UopGroups[action];
            }
            else if (index.Groups != null && action < index.Groups.Length)
            {
                groupObj = index.Groups[action];
            }

            if (groupObj == null)
            {
                return Span<SpriteInfo>.Empty;
            }

            ref var animDir = ref groupObj.Direction[dir];

            if (animDir.Address == uint.MaxValue)
            {
                return Span<SpriteInfo>.Empty;
            }

            Span<AnimationsLoader.FrameInfo> frames;

            if (animDir.FrameCount <= 0 && animDir.SpriteInfos == null)
            {
                // EC override: AMOU frames substitute the CC source when
                // EcAnimation.UseEc is on and we have data for this body/
                // action. Falls back to CC if EC has no entry.
                var ecFrames = TryBuildEcFrames(id, action, dir);
                if (ecFrames != null)
                {
                    frames = ecFrames.AsSpan();
                }
                else if (useUOP
                //animDir.IsUOP ||
                ///* If it's not flagged as UOP, but there is no mul data, try to load
                //* it as a UOP anyway. */
                //(animDir.Address == 0 && animDir.Size == 0)
                )
                {
                    var uopGroupObj = (AnimationGroupUop)groupObj;
                    var ff = new AnimationsLoader.AnimationDirection()
                    {
                        Position = uopGroupObj.Offset,
                        Size = uopGroupObj.CompressedLength,
                        UncompressedSize = uopGroupObj.DecompressedLength,
                        CompressionType = uopGroupObj.CompressionType
                    };

                    frames = _animationLoader.ReadUOPAnimationFrames(
                        id,
                        action,
                        dir,
                        index.Type,
                        index.FileIndex,
                        ff
                    );
                }
                else
                {
                    var ff = new AnimationsLoader.AnimationDirection()
                    {
                        Position = groupObj.Direction[dir].Address,
                        Size = groupObj.Direction[dir].Size,
                    };

                    frames = _animationLoader.ReadMULAnimationFrames(index.FileIndex, ff);
                }

                if (frames.IsEmpty)
                {
                    animDir.FrameCount = 0;
                    animDir.SpriteInfos = Array.Empty<SpriteInfo>();
                    return Span<SpriteInfo>.Empty;
                }

                animDir.FrameCount = (byte)frames.Length;
                animDir.SpriteInfos = new SpriteInfo[frames.Length];

                for (int i = 0; i < frames.Length; i++)
                {
                    ref var frame = ref frames[i];
                    ref var spriteInfo = ref animDir.SpriteInfos[frame.Num];

                    if (frame.Width <= 0 || frame.Height <= 0)
                    {
                        spriteInfo = SpriteInfo.Empty;

                        /* Missing frame. */
                        continue;
                    }

                    uint keyUpper = (uint)((action | (dir << 8) | ((useUOP ? 1 : 0) << 16)));
                    uint keyLower = (uint)((id | (frame.Num << 16)));
                    ulong key = (keyLower | ((ulong)keyUpper << 32));

                    _picker.Set(key, frame.Width, frame.Height, frame.Pixels);

                    spriteInfo.Center.X = frame.CenterX;
                    spriteInfo.Center.Y = frame.CenterY;
                    spriteInfo.Texture = _atlas.AddSprite(
                        frame.Pixels.AsSpan(),
                        frame.Width,
                        frame.Height,
                        out spriteInfo.UV
                    );
                }
            }

            return animDir.SpriteInfos.AsSpan(0, animDir.FrameCount);
        }

        /// <summary>
        /// Build a CC-shaped <see cref="AnimationsLoader.FrameInfo"/> array
        /// for one direction from the EC AMOU cache. Returns null when EC
        /// has no entry for (body, action) or isn't enabled.
        ///
        /// AMOU stores all directions concatenated in one per-action file
        /// (body 400 idle: 50 total frames = 5 dirs × 10 fpd, matching CC's
        /// MAX_DIRECTIONS = 5). Tested 10 dirs × 5 fpd — produced wrong
        /// facings, so the 5-dir layout is correct.
        /// </summary>
        private AnimationsLoader.FrameInfo[] TryBuildEcFrames(ushort body, byte action, byte dir)
        {
            if (Ec == null || !Ec.IsEnabled) return null;

            // CC uses per-body-type action enums (LowAnimationGroup for animals,
            // PeopleAnimationGroup for humans, HighAnimationGroup for monsters).
            // AMOU stores per-body action files under HighAnimationGroup numbering
            // (regardless of body type), so for Animal/SeaMonster bodies whose
            // *effective* group is Low (the default) we translate Low → High.
            //   cow (body 216, no Extended flag): CC asks for Low.Stand=2,
            //     AMOU's 02.bin is High.Die1 → remap 2 → 1 (visually verified).
            // Animal bodies with the CalculateOffsetLowGroupExtended flag (eagle,
            // dragons, etc.) already pass High-group action numbers from CC, so
            // we MUST NOT remap them. Same for the Extended | ByPeopleGroup case
            // (those use People numbering — also passes through unchanged).
            byte amouAction = action;
            var type = GetAnimType(body);
            if (type == AnimationGroupsType.Animal || type == AnimationGroupsType.SeaMonster)
            {
                var flags = GetAnimFlags(body);
                bool extended = (flags & AnimationFlags.CalculateOffsetLowGroupExtended) != 0;
                bool byLow = (flags & AnimationFlags.CalculateOffsetByLowGroup) != 0;
                bool byPeople = (flags & AnimationFlags.CalculateOffsetByPeopleGroup) != 0;
                // Skip remap unless the body's effective group is Low (default
                // path, or Extended explicitly directed to Low via ByLowGroup).
                bool effectiveLow = !extended || byLow;
                if (effectiveLow && !byPeople)
                    amouAction = LowToHighAction(action);
            }

            if (!Ec.TryGetFrames(body, amouAction, out var src)) return null;
            if (src == null || src.Length == 0) return null;

            int dirCount = AnimationsLoader.MAX_DIRECTIONS;
            int fpd = src.Length / dirCount;
            if (fpd <= 0) return null;

            int dirIdx = dir < dirCount ? dir : dirCount - 1;
            int start = dirIdx * fpd;
            var arr = new AnimationsLoader.FrameInfo[fpd];
            for (int i = 0; i < fpd; i++)
            {
                ref var ef = ref src[start + i];
                arr[i].Num = i;
                if (!ef.IsValid) continue;
                arr[i].CenterX = ef.CenterX;
                arr[i].CenterY = ef.CenterY;
                arr[i].Width = (short)ef.Width;
                arr[i].Height = (short)ef.Height;
                arr[i].Pixels = ef.Pixels;
            }
            return arr;
        }

        /// <summary>
        /// Translate a CC LowAnimationGroup action number into the
        /// HighAnimationGroup number AMOU uses for animal/sea-monster bodies.
        /// Empirically verified: cow (body 216) at AMOU action 2 plays Die1,
        /// matching High.Die1 = 2 (not Low.Stand = 2).
        /// </summary>
        private static byte LowToHighAction(byte low)
        {
            // Low enum:  Walk=0, Run=1, Stand=2, Eat=3, Unknown=4, Attack1=5,
            //            Attack2=6, Attack3=7, Die1=8, Fidget1=9, Fidget2=10,
            //            LieDown=11, Die2=12
            // High enum: Walk=0, Stand=1, Die1=2, Die2=3, Attack1=4, Attack2=5,
            //            Attack3=6, Misc1=7, Misc2=8, Misc3=9, Stumble=10,
            //            SlapGround=11, Cast=12, GetHit1=13, Misc4=14,
            //            GetHit2=15, GetHit3=16, Fidget1=17, Fidget2=18,
            //            Fly=19, Land=20, DieInFlight=21
            switch (low)
            {
                case 0:  return 0;   // Walk → Walk
                case 1:  return 0;   // Run → Walk (High has no Run)
                case 2:  return 1;   // Stand → Stand  ← fixes cow idle
                case 3:  return 7;   // Eat → Misc1
                case 4:  return 7;   // Unknown → Misc1
                case 5:  return 4;   // Attack1 → Attack1
                case 6:  return 5;   // Attack2 → Attack2
                case 7:  return 6;   // Attack3 → Attack3
                case 8:  return 2;   // Die1 → Die1
                case 9:  return 17;  // Fidget1 → Fidget1
                case 10: return 18;  // Fidget2 → Fidget2
                case 11: return 14;  // LieDown → Misc4
                case 12: return 3;   // Die2 → Die2
                default: return low;
            }
        }

        public void UpdateAnimationTable(BodyConvFlags flags)
        {
            _animationLoader.ProcessBodyConvDef(flags);
            //if (flags != _lastFlags)
            //{
            //    if (_lastFlags != (BodyConvFlags)(-1))
            //    {
            //        /* This happens when you log out of an account then into another
            //         * one with different expansions activated. Just reload the anim
            //         * files from scratch. */
            //        Array.Clear(_dataIndex, 0, _dataIndex.Length);
            //        LoadInternal();
            //    }

            //    ProcessBodyConvDef(flags);
            //}

            //_lastFlags = flags;
        }

        public void ConvertBodyIfNeeded(
            ref ushort graphic,
            bool isParent = false,
            bool forceUOP = false,
            bool isCorpse = false
        )
        {
            if (graphic >= _dataIndex.Length)
                return;

            ushort hue = 0;

            if (_dataIndex[graphic] != null && _dataIndex[graphic].FileIndex == 0 && (_dataIndex[graphic].Flags & AnimationFlags.UseUopAnimation) == 0)
                _ = isCorpse ? _animationLoader.ReplaceCorpse(ref graphic, ref hue) : _animationLoader.ReplaceBody(ref graphic, ref hue);
        }

        public bool AnimationExists(ushort graphic, byte group, bool isCorpse = false)
        {
            if (graphic < _dataIndex.Length && group < AnimationsLoader.MAX_ACTIONS)
            {
                var frames = GetAnimationFrames(
                    graphic,
                    group,
                    0,
                    out var _,
                    out _,
                    false,
                    isCorpse
                );

                return !frames.IsEmpty && frames[0].Texture != null;
            }

            return false;
        }

        private sealed class IndexAnimation
        {
            public int FileIndex;
            public ushort Hue;
            public AnimationFlags Flags;
            public AnimationGroup[] Groups;
            public AnimationGroupUop[] UopGroups;
            public AnimationGroupsType Type = AnimationGroupsType.Unknown;
        }
    }
}