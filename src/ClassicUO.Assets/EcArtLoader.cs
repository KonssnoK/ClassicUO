// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.IO.Compression;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Loads Enhanced-Client static art (DDS) from <c>Texture.uop</c> (HD) and
    /// <c>LegacyTexture.uop</c> (DXT re-encoded legacy art). Layered on top of
    /// the classic <see cref="ArtLoader"/>: an art id is first looked up in the
    /// HD archive, then the legacy DDS archive, then the caller falls back to
    /// the classic <c>art.mul</c> / <c>artLegacyMUL.uop</c>.
    ///
    /// Naming patterns (lowercased, Jenkins-hashed per <see cref="UOFileUop"/>):
    ///   Texture.uop        -> build/worldart/{id:D8}.dds
    ///   LegacyTexture.uop  -> build/tileartlegacy/{id:D8}.dds
    ///
    /// See tools/ec_research/ec/patterns.py for the canonical pattern list.
    /// </summary>
    public sealed class EcArtLoader : UOFileLoader
    {
        private UOFileUop _enhanced;
        private UOFileUop _legacy;

        public EcArtLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public bool IsEnabled => _enhanced != null || _legacy != null;
        public UOFileUop EnhancedFile => _enhanced;
        public UOFileUop LegacyFile   => _legacy;

        public override void Load()
        {
            if (!FileManager.HasEnhancedClient)
            {
                return;
            }

            string ecRoot = FileManager.EnhancedClientPath;
            string hdPath     = Path.Combine(ecRoot, "Texture.uop");
            string legacyPath = Path.Combine(ecRoot, "LegacyTexture.uop");

            try
            {
                if (File.Exists(hdPath))
                {
                    _enhanced = new UOFileUop(hdPath, "build/worldart/{0:D8}.dds");
                    _enhanced.FillEntries();
                    Log.Trace($"EcArt: opened {hdPath} ({_enhanced.Entries?.Length ?? 0} entries)");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"EcArt: failed to open Texture.uop: {ex.Message}");
                _enhanced = null;
            }

            try
            {
                if (File.Exists(legacyPath))
                {
                    _legacy = new UOFileUop(legacyPath, "build/tileartlegacy/{0:D8}.dds");
                    _legacy.FillEntries();
                    Log.Trace($"EcArt: opened {legacyPath} ({_legacy.Entries?.Length ?? 0} entries)");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"EcArt: failed to open LegacyTexture.uop: {ex.Message}");
                _legacy = null;
            }
        }

        /// <summary>
        /// Direct lookup by CC art_id — this is how EC's static-placement
        /// loader actually keys sprites (verified via Ghidra; SUB_9_7 records
        /// are for 3D model surface textures, not 2D placement). For statics
        /// (art_id >= 0x4000) the key is <c>art_id - 0x4000</c>; for land
        /// tiles the art_id itself is used.
        /// HD is tried first; falls back to legacy if HD has no entry.
        /// </summary>
        public bool TryGetDdsByArtId(int artId, out byte[] dds, out bool fromHd)
        {
            dds = null;
            fromHd = false;
            if (artId < 0) return false;

            int itemId = artId >= 0x4000 ? artId - 0x4000 : artId;

            // Legacy first — kept for backward compat callers. Use the
            // split TryGetHdByArtId / TryGetLegacyByArtId for the EC
            // convention (HD only when EcImage is populated).
            if (_legacy != null
                && TryReadFromArchive(_legacy, "build/tileartlegacy/", itemId, out dds))
            {
                return true;
            }
            return false;
        }

        public bool TryGetHdByArtId(int artId, out byte[] dds)
        {
            dds = null;
            if (artId < 0 || _enhanced == null) return false;
            int itemId = artId >= 0x4000 ? artId - 0x4000 : artId;
            return TryReadFromArchive(_enhanced, "build/worldart/", itemId, out dds);
        }

        public bool TryGetLegacyByArtId(int artId, out byte[] dds)
        {
            dds = null;
            if (artId < 0 || _legacy == null) return false;
            int itemId = artId >= 0x4000 ? artId - 0x4000 : artId;
            return TryReadFromArchive(_legacy, "build/tileartlegacy/", itemId, out dds);
        }

        /// <summary>
        /// Loads the per-sprite hue mask DDS (alpha channel = which pixels
        /// are hueable, used by EC's mask-based partial-hue shader). The
        /// mask lives in <c>LegacyTexture.uop</c> at
        /// <c>build/tileartlegacy/{item_id + 1000000:08}.dds</c>.
        /// Returns null when no mask is shipped for this sprite.
        /// </summary>
        public bool TryGetMaskByArtId(int artId, out byte[] dds)
        {
            dds = null;
            if (artId < 0) return false;
            int itemId = artId >= 0x4000 ? artId - 0x4000 : artId;
            // Both archives use the same {1_000_000+id:08}.dds naming for
            // the per-sprite hue mask. Try HD first (matches our HD-first
            // texture preference); fall back to legacy.
            if (_enhanced != null
                && TryReadFromArchive(_enhanced, "build/worldart/", 1_000_000 + itemId, out dds))
            {
                return true;
            }
            if (_legacy != null
                && TryReadFromArchive(_legacy, "build/tileartlegacy/", 1_000_000 + itemId, out dds))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fetch a DDS by namespaced sprite reference (the kind produced by
        /// <see cref="EcTileArtLoader"/> after resolving SUB_9_7 dictionary
        /// offsets to filenames).
        ///   WorldArt        -> Texture.uop / build/worldart/{id:08}.dds (HD)
        ///   TileArtLegacy   -> LegacyTexture.uop / build/tileartlegacy/{id:08}.dds
        ///   TileArtEnhanced -> would be EnhancedTexture.uop (not shipped in
        ///                       most builds); returns false here.
        /// </summary>
        public bool TryGetDdsByRef(EcSpriteRef sref, out byte[] dds, out bool fromHd)
        {
            dds = null;
            fromHd = false;
            if (sref.SpriteId < 0) return false;

            switch (sref.Namespace)
            {
                case EcSpriteNamespace.WorldArt:
                    if (_enhanced != null
                        && TryReadFromArchive(_enhanced, "build/worldart/", sref.SpriteId, out dds))
                    {
                        fromHd = true;
                        return true;
                    }
                    return false;

                case EcSpriteNamespace.TileArtLegacy:
                    return _legacy != null
                        && TryReadFromArchive(_legacy, "build/tileartlegacy/", sref.SpriteId, out dds);

                case EcSpriteNamespace.TileArtEnhanced:
                    // EnhancedTexture.uop isn't shipped in the current build;
                    // fall back to CC art when this is the only reference.
                    return false;
            }
            return false;
        }

        private static bool TryReadFromArchive(UOFileUop uop, string folder, int artId, out byte[] dds)
        {
            dds = null;
            ulong hash = UOFileUop.CreateHash(folder + artId.ToString("D8") + ".dds");
            if (!uop.TryGetUOPData(hash, out UOFileIndex entry) || entry.Equals(UOFileIndex.Invalid))
            {
                return false;
            }

            uop.Seek(entry.Offset, SeekOrigin.Begin);
            int compressed = entry.Length;
            if (compressed <= 0)
            {
                return false;
            }

            byte[] raw = new byte[compressed];
            uop.Read(raw);

            if (entry.CompressionFlag == CompressionType.Zlib)
            {
                // zlib stream = 2-byte header + raw DEFLATE
                using var ms = new MemoryStream(raw, 2, raw.Length - 2);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream(entry.DecompressedLength > 0 ? entry.DecompressedLength : compressed * 4);
                deflate.CopyTo(outMs);
                dds = outMs.ToArray();
            }
            else
            {
                dds = raw;
            }
            return true;
        }

        public override void ClearResources()
        {
            _enhanced?.Dispose();
            _enhanced = null;
            _legacy?.Dispose();
            _legacy = null;
        }
    }
}
