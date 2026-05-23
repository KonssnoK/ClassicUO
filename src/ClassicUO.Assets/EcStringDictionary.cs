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
    /// Reads the EC string_dictionary.uop blob and provides
    /// "sd_off -> string" lookups used by tileart's SUB_9_7 texture refs.
    ///
    /// Format of the inner blob (build/stringdictionary/string_dictionary.bin):
    ///   16-byte header (magic + counts; not needed for lookup)
    ///   repeating: u16 length, [length] bytes ASCII content
    /// Strings are NOT null-terminated. The tileart sd_off is a *byte* offset
    /// that lands somewhere inside one of the content ranges; we binary-search
    /// to find the containing entry and return its full content.
    ///
    /// Verified end-to-end against tools/ec_research/scripts/58_real_lookup.py.
    /// </summary>
    public sealed class EcStringDictionary : UOFileLoader
    {
        private UOFileUop _file;
        // Parallel arrays sorted by content_start for binary search.
        private int[] _starts = Array.Empty<int>();
        private int[] _ends   = Array.Empty<int>();   // exclusive
        private string[] _contents = Array.Empty<string>();

        public EcStringDictionary(UOFileManager fileManager) : base(fileManager) { }

        public bool IsLoaded => _starts.Length > 0;
        public int EntryCount => _starts.Length;

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
                Log.Trace($"EcStringDictionary: loaded {_starts.Length} entries from {path}");
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
            // First pass: count entries to size the arrays exactly.
            int p = 16;
            int n = blob.Length;
            int count = 0;
            while (p + 2 <= n)
            {
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p));
                if (len == 0 || len > 500) break;
                if (p + 2 + len > n) break;
                count++;
                p += 2 + len;
            }

            _starts = new int[count];
            _ends   = new int[count];
            _contents = new string[count];

            p = 16;
            int i = 0;
            while (i < count && p + 2 <= n)
            {
                ushort len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p));
                int contentStart = p + 2;
                int contentEnd   = contentStart + len;
                // Range is [prefix_start, content_end): sd_off may legitimately
                // land on the u16 length prefix bytes (seen on tile 200 etc.)
                _starts[i] = p;
                _ends[i]   = contentEnd;
                _contents[i] = System.Text.Encoding.ASCII.GetString(blob, contentStart, len);
                p = contentEnd;
                i++;
            }
        }

        /// <summary>
        /// Returns the dictionary string whose byte range contains <paramref name="offset"/>.
        /// </summary>
        public string GetStringAtOffset(int offset)
        {
            if (_starts.Length == 0) return null;

            // Binary search by start; check range.
            int lo = 0, hi = _starts.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (offset < _starts[mid])      hi = mid;
                else if (offset >= _ends[mid])  lo = mid + 1;
                else                             return _contents[mid];
            }
            return null;
        }

        public override void ClearResources()
        {
            _file?.Dispose();
            _file = null;
            _starts = Array.Empty<int>();
            _ends = Array.Empty<int>();
            _contents = Array.Empty<string>();
        }
    }
}
