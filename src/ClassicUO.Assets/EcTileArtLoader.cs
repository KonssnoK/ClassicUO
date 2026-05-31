// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Sprite reference resolved from a tileart record's SUB_9_7 entry. Each
    /// dictionary string identifies a namespace and a numeric sprite id; the
    /// runtime hashes "build/<namespace>/<sprite_id:08>.dds" to find the DDS.
    /// </summary>
    public enum EcSpriteNamespace : byte
    {
        Unknown        = 0,
        WorldArt       = 1,   // -> Texture.uop (HD)
        TileArtLegacy  = 2,   // -> LegacyTexture.uop (legacy DDS)
        TileArtEnhanced = 3,  // -> EnhancedTexture.uop (not shipped in most builds)
    }

    public readonly struct EcSpriteRef
    {
        public readonly EcSpriteNamespace Namespace;
        public readonly int SpriteId;
        public readonly float TextureRepetition;

        public EcSpriteRef(EcSpriteNamespace ns, int id, float rep)
        {
            Namespace = ns; SpriteId = id; TextureRepetition = rep;
        }
    }

    /// <summary>
    /// Per-tile metadata properties (SUB_9 / SUB_9_2 in the tileart record).
    /// </summary>
    public enum EcTileArtProperty : byte
    {
        Weight     = 0,
        Quality    = 1,
        Quantity   = 2,
        Height     = 3,
        Value      = 4,
        AcVc       = 5,
        Slot       = 6,
        OffC8      = 7,
        Appearance = 8,
        Race       = 9,
        Gender     = 10,
        Paperdoll  = 11,
    }

    /// <summary>
    /// Sprite bounds + anchor offset, stored verbatim in the tileart record at
    /// offsets 0x4D (EC art) and 0x65 (legacy 2D). Six little-endian int32s.
    /// </summary>
    public struct EcImageOffset
    {
        public int X0, Y0, X1, Y1;
        public int PixelsXOffset, PixelsYOffset;
        public int Width  => X1 - X0;
        public int Height => Y1 - Y0;
        public bool IsPopulated => X1 != 0 || Y1 != 0;
    }

    /// <summary>
    /// Fully-decoded EC tileart record. The texture references in
    /// <see cref="WorldArt"/>, <see cref="TileArtLegacy"/>, and
    /// <see cref="TileArtEnhanced"/> hold the resolved sprite ids the
    /// renderer should use.
    /// </summary>
    public sealed class EcTileArtData
    {
        public int Version;
        public uint StringId;
        public int TileId;
        public byte UnkBool, UnkByte;
        public float HeaderFloatA, HeaderFloatB;
        public uint OldId;
        public float LightFloatA, LightFloatB;
        public ulong FlagsEc, FlagsLegacy;
        public EcImageOffset EcImage, LegacyImage;

        public List<(EcTileArtProperty, uint)> PropsEc     = new();
        public List<(EcTileArtProperty, uint)> PropsLegacy = new();

        public List<EcSpriteRef> WorldArt        = new();
        public List<EcSpriteRef> TileArtLegacy   = new();
        public List<EcSpriteRef> TileArtEnhanced = new();
        public List<EcSpriteRef> Textures        = new();

        public byte RadarR, RadarG, RadarB, RadarA;
        public byte[] EffectsTail = Array.Empty<byte>();

        public uint Weight     => Find(PropsEc, EcTileArtProperty.Weight);
        public uint Height     => Find(PropsEc, EcTileArtProperty.Height);
        public uint Layer      => Find(PropsEc, EcTileArtProperty.Slot);
        public uint Appearance => Find(PropsEc, EcTileArtProperty.Appearance);

        private static uint Find(List<(EcTileArtProperty Prop, uint Value)> list, EcTileArtProperty p)
        {
            for (int i = 0; i < list.Count; i++) if (list[i].Prop == p) return list[i].Value;
            return 0;
        }

        /// <summary>
        /// Preferred sprite for rendering: HD via WorldArt first, then legacy.
        /// Returns false if neither group has a usable reference in this build.
        /// </summary>
        public bool TryPrimary(out EcSpriteRef sref)
        {
            if (WorldArt.Count > 0)        { sref = WorldArt[0];        return true; }
            if (TileArtEnhanced.Count > 0) { sref = TileArtEnhanced[0]; return true; }
            if (TileArtLegacy.Count > 0)   { sref = TileArtLegacy[0];   return true; }
            sref = default;
            return false;
        }
    }

    /// <summary>
    /// Loads <c>tileart.uop</c> from the EC folder, parses the SUB_9_7 texture
    /// references per the project owner's wiki spec, and resolves each
    /// dictionary offset to a sprite reference via <see cref="EcStringDictionary"/>.
    ///
    /// Resolution chain (verified end-to-end in
    /// tools/ec_research/scripts/58_real_lookup.py):
    ///   1. tile_id -> tileart record (hash: build/tileart/{id:D8}.bin)
    ///   2. Parse SUB_9_7 groups; each texture entry holds:
    ///        u32 sd_off, u8, f32 TextureRepetition, u32, u32
    ///   3. <see cref="EcStringDictionary"/>.GetStringAtOffset(sd_off)
    ///      -> "Data\WorldArt\00000461_Rattan_Wall.tga" (or similar)
    ///   4. Parse namespace + sprite id from the filename.
    ///
    /// The renderer then hashes "build/{namespace_lower}/{sprite_id:D8}.dds"
    /// into Texture.uop (HD) or LegacyTexture.uop.
    /// </summary>
    public sealed class EcTileArtLoader : UOFileLoader
    {
        private static readonly Regex RxWorldArt =
            new(@"Data\\WorldArt\\(\d+)(?:_[^.]*)?\.tga", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxLegacy =
            new(@"Data\\TileArtLegacy\\(\d+)\.tga",        RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RxEnhanced =
            new(@"Data\\TileArtEnhanced\\(\d+)\.tga",      RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private UOFileUop _file;
        private readonly Dictionary<int, EcTileArtData> _cache = new();

        public EcTileArtLoader(UOFileManager fileManager) : base(fileManager) { }

        public bool IsEnabled => _file != null;
        public UOFileUop File => _file;

        public override void Load()
        {
            if (!FileManager.HasEnhancedClient) return;

            string path = Path.Combine(FileManager.EnhancedClientPath, "tileart.uop");
            if (!System.IO.File.Exists(path))
            {
                Log.Trace($"EcTileArt: tileart.uop not found at {path}");
                return;
            }

            try
            {
                _file = new UOFileUop(path, "build/tileart/{0:D8}.bin");
                _file.FillEntries();
                Log.Trace($"EcTileArt: opened {path} ({_file.Entries?.Length ?? 0} entries)");
            }
            catch (Exception ex)
            {
                Log.Warn($"EcTileArt: failed to open tileart.uop: {ex.Message}");
                _file = null;
            }
        }

        public bool TryGet(int tileId, out EcTileArtData data)
        {
            data = null;
            if (_file == null) return false;

            // EC's tileart.uop is keyed by RAW item_id, not art_id. For
            // statics, callers usually pass `graphic + 0x4000` (the CC art
            // id); strip the offset before forming the lookup key. Both
            // forms hash to DIFFERENT records in the UOP — the raw-id form
            // has the actual sprite metadata (EcImage rect + LegacyImage
            // rect + dx/dy padding), while the art-id form is a stub.
            // Without this strip we'd silently read empty (0,0,0,0,0,0)
            // EcImage on every static and fall back to legacy DDS.
            int rawId = tileId >= 0x4000 ? tileId - 0x4000 : tileId;
            if (_cache.TryGetValue(rawId, out data)) return data != null;

            ulong hash = UOFileUop.CreateHash($"build/tileart/{rawId:D8}.bin");
            if (!_file.TryGetUOPData(hash, out UOFileIndex entry) || entry.Equals(UOFileIndex.Invalid))
            {
                _cache[rawId] = null;
                return false;
            }

            byte[] payload = ReadEntry(entry);
            if (payload == null) { _cache[rawId] = null; return false; }

            try
            {
                data = Parse(payload);
                _cache[tileId] = data;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"EcTileArt: failed to parse id {rawId}: {ex.Message}");
                _cache[rawId] = null;
                return false;
            }
        }

        private byte[] ReadEntry(UOFileIndex entry)
        {
            _file.Seek(entry.Offset, SeekOrigin.Begin);
            int compressed = entry.Length;
            if (compressed <= 0) return null;
            byte[] raw = new byte[compressed];
            _file.Read(raw);
            if (entry.CompressionFlag == CompressionType.Zlib)
            {
                using var ms = new MemoryStream(raw, 2, raw.Length - 2);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream(entry.DecompressedLength > 0
                                                   ? entry.DecompressedLength
                                                   : compressed * 4);
                deflate.CopyTo(outMs);
                return outMs.ToArray();
            }
            return raw;
        }

        // -------- record parser --------

        private EcTileArtData Parse(byte[] buf)
        {
            var r = new SpanReader(buf);
            var d = new EcTileArtData();

            d.Version       = r.U16();
            d.StringId      = r.U32();
            d.TileId        = (int)r.U32();
            d.UnkBool       = r.U8();
            d.UnkByte       = r.U8();
            d.HeaderFloatA  = r.F32();
            d.HeaderFloatB  = r.F32();
            r.U32(); // fixedZero
            d.OldId         = r.U32();
            r.U32(); r.U32();
            r.U8();  r.F32(); r.U32();
            d.LightFloatA   = r.F32();
            d.LightFloatB   = r.F32();
            r.U32();
            d.FlagsEc       = r.U64();
            d.FlagsLegacy   = r.U64();
            r.U32();
            d.EcImage     = ReadImage(ref r);
            d.LegacyImage = ReadImage(ref r);

            // SUB_9 / SUB_9_2 properties
            int cnt = r.U8();
            for (int i = 0; i < cnt; i++) d.PropsEc.Add(((EcTileArtProperty)r.U8(), r.U32()));
            cnt = r.U8();
            for (int i = 0; i < cnt; i++) d.PropsLegacy.Add(((EcTileArtProperty)r.U8(), r.U32()));

            // SUB_9_3 money items
            uint c32 = r.U32();
            for (uint i = 0; i < c32; i++) { r.U32(); r.U32(); }

            // SUB_9_4 animation appearance filter
            c32 = r.U32();
            for (uint i = 0; i < c32; i++)
            {
                byte v = r.U8();
                if (v == 0) { uint sub = r.U32(); for (uint k = 0; k < sub; k++) { r.U32(); r.U32(); } }
                else if (v == 1) { r.U8(); r.U32(); }
                // Per UOReader: subval != 0 && != 1 → just skip this item
                // (continue), DO NOT break out of the loop or every
                // subsequent field gets shifted and the SUB_9_7 texture
                // refs come out as garbage (tile 521 case).
            }

            // SUB_9_5 sitting
            byte sittingCount = r.U8();
            if (sittingCount != 0) { r.U32(); r.U32(); r.U32(); r.U32(); }

            // SUB_9_6 radar RGBA
            d.RadarR = r.U8(); d.RadarG = r.U8(); d.RadarB = r.U8(); d.RadarA = r.U8();

            // SUB_9_7: four texture groups, each in TEXTURE() format from the wiki.
            var dict = FileManager.EcStringDictionary;
            ParseTextureGroup(ref r, dict, d.WorldArt);
            ParseTextureGroup(ref r, dict, d.TileArtLegacy);
            ParseTextureGroup(ref r, dict, d.TileArtEnhanced);
            ParseTextureGroup(ref r, dict, d.Textures);

            // Effects (SUB_9_8) — kept opaque for now.
            d.EffectsTail = r.RemainingBytes();
            return d;
        }

        private static EcImageOffset ReadImage(ref SpanReader r) => new()
        {
            X0 = (int)r.U32(), Y0 = (int)r.U32(),
            X1 = (int)r.U32(), Y1 = (int)r.U32(),
            PixelsXOffset = (int)r.U32(), PixelsYOffset = (int)r.U32(),
        };

        private static void ParseTextureGroup(ref SpanReader r, EcStringDictionary dict,
                                              List<EcSpriteRef> dest)
        {
            // Layout from the wiki Texture.creole page:
            //   BYTE Val
            //   if Val != 0:
            //     BYTE
            //     DWORD Shader
            //     BYTE Count
            //     for each: { DWORD sd_off, BYTE, FLOAT texRep, DWORD, DWORD }   = 17 bytes
            //     DWORD Count;   skip Count × DWORD
            //     DWORD Count;   skip Count × DWORD
            byte val = r.U8();
            if (val == 0) return;
            r.U8();            // unknown
            r.U32();           // Shader
            byte count = r.U8();
            for (int i = 0; i < count; i++)
            {
                uint sdOff = r.U32();
                r.U8();
                float texRep = r.F32();
                r.U32();
                r.U32();
                if (dict != null)
                {
                    string s = dict.GetStringAtOffset((int)sdOff);
                    var sref = ResolveSpriteRef(s, texRep);
                    if (sref.Namespace != EcSpriteNamespace.Unknown)
                        dest.Add(sref);
                }
            }
            uint c2 = r.U32();
            for (uint i = 0; i < c2; i++) r.U32();
            uint c3 = r.U32();
            for (uint i = 0; i < c3; i++) r.U32();
        }

        private static EcSpriteRef ResolveSpriteRef(string filename, float texRep)
        {
            if (string.IsNullOrEmpty(filename))
                return new EcSpriteRef(EcSpriteNamespace.Unknown, -1, texRep);

            var m = RxWorldArt.Match(filename);
            if (m.Success)
                return new EcSpriteRef(EcSpriteNamespace.WorldArt, int.Parse(m.Groups[1].Value), texRep);
            m = RxLegacy.Match(filename);
            if (m.Success)
                return new EcSpriteRef(EcSpriteNamespace.TileArtLegacy, int.Parse(m.Groups[1].Value), texRep);
            m = RxEnhanced.Match(filename);
            if (m.Success)
                return new EcSpriteRef(EcSpriteNamespace.TileArtEnhanced, int.Parse(m.Groups[1].Value), texRep);

            return new EcSpriteRef(EcSpriteNamespace.Unknown, -1, texRep);
        }

        public override void ClearResources()
        {
            _file?.Dispose();
            _file = null;
            _cache.Clear();
        }
    }

    internal ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _buf;
        public int Pos;
        public SpanReader(ReadOnlySpan<byte> buf) { _buf = buf; Pos = 0; }
        public byte U8()   { byte v = _buf[Pos]; Pos += 1; return v; }
        public ushort U16(){ ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.Slice(Pos)); Pos += 2; return v; }
        public uint U32()  { uint v   = BinaryPrimitives.ReadUInt32LittleEndian(_buf.Slice(Pos)); Pos += 4; return v; }
        public ulong U64() { ulong v  = BinaryPrimitives.ReadUInt64LittleEndian(_buf.Slice(Pos)); Pos += 8; return v; }
        public float F32() { float v  = BinaryPrimitives.ReadSingleLittleEndian(_buf.Slice(Pos)); Pos += 4; return v; }
        public byte[] RemainingBytes() { byte[] a = _buf.Slice(Pos).ToArray(); Pos = _buf.Length; return a; }
    }
}
