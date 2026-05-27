// SPDX-License-Identifier: BSD-2-Clause
//
// Enhanced-Client animation frame archive loader. Opens AnimationFrame{1..6}.uop
// and exposes raw AMOU payloads keyed by (body_id, action). Decoding lives in
// ClassicUO.Renderer.Animations.EcAnimation (porting the verified Python
// reference at tools/ec_research/scripts/amou_decode_verified.py, itself
// ported from UOReader 0.8.7 by Kons).
//
// UOP key naming: build/animationframe/{body:D6}/{action:D2}.bin
// Format spec: tools/ec_research/docs/AnimationFrame_AMOU.md

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.IO.Compression;

namespace ClassicUO.Assets
{
    public sealed class EcAnimationLoader : UOFileLoader
    {
        private readonly UOFileUop[] _files = new UOFileUop[6];

        public EcAnimationLoader(UOFileManager fileManager) : base(fileManager)
        {
        }

        public bool IsEnabled
        {
            get
            {
                for (int i = 0; i < _files.Length; i++)
                    if (_files[i] != null) return true;
                return false;
            }
        }

        public override void Load()
        {
            if (!FileManager.HasEnhancedClient) return;
            string root = FileManager.EnhancedClientPath;

            for (int i = 0; i < _files.Length; i++)
            {
                string path = Path.Combine(root, $"AnimationFrame{i + 1}.uop");
                if (!File.Exists(path)) continue;
                try
                {
                    // Pattern argument is unused by us — we hash names directly.
                    var uop = new UOFileUop(path, "build/animationframe/{0:D8}.bin");
                    uop.FillEntries();
                    _files[i] = uop;
                    Log.Trace($"EcAnim: opened {path}");
                }
                catch (Exception ex)
                {
                    Log.Warn($"EcAnim: failed to open {path}: {ex.Message}");
                }
            }
        }

        public bool TryGet(int body, int action, out byte[] payload)
        {
            payload = null;
            if (body < 0 || action < 0) return false;

            string name = $"build/animationframe/{body:D6}/{action:D2}.bin";
            ulong hash = UOFileUop.CreateHash(name);

            for (int i = 0; i < _files.Length; i++)
            {
                if (_files[i] == null) continue;
                if (TryReadFromArchive(_files[i], hash, out payload))
                    return true;
            }
            return false;
        }

        private static bool TryReadFromArchive(UOFileUop uop, ulong hash, out byte[] data)
        {
            data = null;
            if (!uop.TryGetUOPData(hash, out UOFileIndex entry) || entry.Equals(UOFileIndex.Invalid))
                return false;

            uop.Seek(entry.Offset, SeekOrigin.Begin);
            int compressed = entry.Length;
            if (compressed <= 0) return false;

            byte[] raw = new byte[compressed];
            uop.Read(raw);

            if (entry.CompressionFlag == CompressionType.Zlib)
            {
                using var ms = new MemoryStream(raw, 2, raw.Length - 2);
                using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream(entry.DecompressedLength > 0 ? entry.DecompressedLength : compressed * 4);
                deflate.CopyTo(outMs);
                data = outMs.ToArray();
            }
            else
            {
                data = raw;
            }
            return true;
        }

        public override void ClearResources()
        {
            for (int i = 0; i < _files.Length; i++)
            {
                _files[i]?.Dispose();
                _files[i] = null;
            }
        }
    }
}
