// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility.Logging;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ClassicUO.Assets
{
    /// <summary>
    /// Reads the EC string_dictionary.uop blob and provides index-based
    /// string lookups used by tileart's SUB_9 texture refs and elsewhere.
    ///
    /// Format of the inner blob (build/stringdictionary/string_dictionary.bin):
    ///   14-byte header: i64 unk + u32 StringCount + i16 unk
    ///   StringCount entries of: u16 length, [length] bytes ASCII content
    /// The tileart "sd_off" value is NOT a byte offset — it is the 0-based
    /// INDEX into the string list, matching UOReader's GetStringAtPosition.
    /// </summary>
    public sealed class EcStringDictionary : UOFileLoader
    {
        private UOFileUop _file;
        private string[] _strings = Array.Empty<string>();

        public EcStringDictionary(UOFileManager fileManager) : base(fileManager) { }

        public bool IsLoaded => _strings.Length > 0;
        public int EntryCount => _strings.Length;

        public override void Load()
        {
            if (!FileManager.HasEnhancedClient) return;

            string path = Path.Combine(FileManager.EnhancedClientPath, "string_dictionary.uop");
            if (!System.IO.File.Exists(path))
            {
                Log.Trace($"EcStringDictionary: not found at {path}");
                return;
            }

            try
            {
                _file = new UOFileUop(path, "build/stringdictionary/string_dictionary.bin");
                _file.FillEntries();
                ulong h = UOFileUop.CreateHash("build/stringdictionary/string_dictionary.bin");
                if (!_file.TryGetUOPData(h, out UOFileIndex entry) || entry.Equals(UOFileIndex.Invalid))
                {
                    Log.Warn("EcStringDictionary: entry hash not found in string_dictionary.uop");
                    return;
                }

                byte[] blob = ReadEntry(entry);
                if (blob == null) return;

                ParseBlob(blob);
                Log.Trace($"EcStringDictionary: loaded {_strings.Length} entries from {path}");
            }
            catch (Exception ex)
            {
                Log.Warn($"EcStringDictionary: failed to load: {ex.Message}");
                _file = null;
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

        private void ParseBlob(byte[] blob)
        {
            // Header: i64 unk + u32 StringCount + i16 unk = 14 bytes.
            if (blob.Length < 14) return;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(8));
            _strings = new string[count];

            int p = 14;
            int n = blob.Length;
            for (int i = 0; i < count && p + 2 <= n; i++)
            {
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p));
                p += 2;
                if (p + len > n) break;
                _strings[i] = System.Text.Encoding.ASCII.GetString(blob, p, len);
                p += len;
            }
        }

        /// <summary>
        /// Returns the dictionary string at the given 0-based index.
        /// Mirrors UOReader's StringDictionary.GetStringAtPosition.
        /// </summary>
        public string GetStringAtOffset(int index)
        {
            if (_strings.Length == 0) return null;
            if ((uint)index >= (uint)_strings.Length) return _strings[0];
            return _strings[index];
        }

        public override void ClearResources()
        {
            _file?.Dispose();
            _file = null;
            _strings = Array.Empty<string>();
        }
    }
}
