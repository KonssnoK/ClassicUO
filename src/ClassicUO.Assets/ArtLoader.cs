// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.IO;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using System;
using System.IO;
using System.Threading.Tasks;

namespace ClassicUO.Assets
{
    public sealed class ArtLoader : UOFileLoader
    {
        private UOFile _file;
        private UOFileUop _hdFile;
        public const int MAX_LAND_DATA_INDEX_COUNT = 0x4000;
        public const int MAX_STATIC_DATA_INDEX_COUNT = 0x14000;

        private const uint HD_MAGIC = 0x44485543u; // 'CUHD'
        private const int HD_SCALE = 4;

        public ArtLoader(UOFileManager fileManager) : base(fileManager)
        {
        }


        public UOFile File => _file;
        public bool HDArtAvailable => _hdFile != null;


        public override void Load()
        {
            string filePath = FileManager.GetUOFilePath("artLegacyMUL.uop");

            if (FileManager.IsUOPInstallation && System.IO.File.Exists(filePath))
            {
                _file = new UOFileUop(filePath, "build/artlegacymul/{0:D8}.tga");
            }
            else
            {
                filePath = FileManager.GetUOFilePath("art.mul");
                string idxPath = FileManager.GetUOFilePath("artidx.mul");

                if (System.IO.File.Exists(filePath) && System.IO.File.Exists(idxPath))
                {
                    _file = new UOFileMul(filePath, idxPath);
                }
            }

            _file.FillEntries();

            if (FileManager.HDArtEnabled)
            {
                var hdPath = FileManager.GetUOFilePath("artLegacyMUL_HD.uop");
                if (System.IO.File.Exists(hdPath))
                {
                    _hdFile = new UOFileUop(hdPath, "build/artlegacymul_hd/{0:D8}.bin");
                    _hdFile.FillEntries();
                    Log.Trace($"HD art enabled: {hdPath}");
                }
                else
                {
                    Log.Warn($"HD art enabled but file not found: {hdPath}");
                }
            }
        }

        private static uint[] TryLoadHD(UOFile hdFile, uint idx, out short width, out short height)
        {
            width = 0;
            height = 0;
            if (hdFile == null)
                return null;

            ref var entry = ref hdFile.GetValidRefEntry((int)idx);
            if (entry.Length == 0 || entry.DecompressedLength == 0)
                return null;

            var compressed = new byte[entry.Length];
            hdFile.Seek(entry.Offset, SeekOrigin.Begin);
            hdFile.Read(compressed);

            byte[] decompressed;
            if (entry.CompressionFlag == CompressionType.Zlib)
            {
                decompressed = new byte[entry.DecompressedLength];
                var err = ZLib.Decompress(compressed, 0, decompressed, decompressed.Length);
                if (err != ZLib.ZLibError.Ok)
                    return null;
            }
            else if (entry.CompressionFlag == CompressionType.None)
            {
                decompressed = compressed;
            }
            else
            {
                return null;
            }

            // CUHD header: u32 magic, u16 version, u16 format, i32 width, i32 height, then BGRA pixels.
            if (decompressed.Length < 16)
                return null;

            uint magic = BitConverter.ToUInt32(decompressed, 0);
            if (magic != HD_MAGIC)
                return null;
            ushort version = BitConverter.ToUInt16(decompressed, 4);
            if (version != 1)
                return null;
            ushort format = BitConverter.ToUInt16(decompressed, 6);
            if (format != 0)
                return null;
            int w = BitConverter.ToInt32(decompressed, 8);
            int h = BitConverter.ToInt32(decompressed, 12);
            if (w <= 0 || h <= 0 || w > 8192 || h > 8192)
                return null;

            int pixelBytes = w * h * 4;
            if (decompressed.Length < 16 + pixelBytes)
                return null;

            var pixels = new uint[w * h];
            Buffer.BlockCopy(decompressed, 16, pixels, 0, pixelBytes);

            width = (short)w;
            height = (short)h;
            return pixels;
        }

        // public Rectangle GetRealArtBounds(int index) =>
        //     index + 0x4000 >= _spriteInfos.Length
        //         ? Rectangle.Empty
        //         : _spriteInfos[index + 0x4000].ArtBounds;

        private static uint[] LoadLand(UOFile file, ref readonly UOFileIndex entry, out short width, out short height)
        {
            if (entry.Length == 0)
            {
                width = 0;
                height = 0;

                return Array.Empty<uint>();
            }

            width = 44;
            height = 44;

            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, SeekOrigin.Begin);

            var data = new uint[width * height];

            for (int i = 0; i < 22; ++i)
            {
                int start = 22 - (i + 1);
                int pos = i * 44 + start;
                int end = start + ((i + 1) << 1);

                for (int j = start; j < end; ++j)
                {
                    data[pos++] = HuesHelper.Color16To32(file.ReadUInt16()) | 0xFF_00_00_00;
                }
            }

            for (int i = 0; i < 22; ++i)
            {
                int pos = (i + 22) * 44 + i;
                int end = i + ((22 - i) << 1);

                for (int j = i; j < end; ++j)
                {
                    data[pos++] = HuesHelper.Color16To32(file.ReadUInt16()) | 0xFF_00_00_00;
                }
            }

            return data;
        }

        private static unsafe uint[] LoadArt(UOFile file, ref readonly UOFileIndex entry, out short width, out short height)
        {
            if (entry.Length == 0)
            {
                width = 0;
                height = 0;

                return Array.Empty<uint>();
            }

            if (entry.File != null)
                file = entry.File;

            file.Seek(entry.Offset, SeekOrigin.Begin);

            var flags = file.ReadUInt32();
            width = file.ReadInt16();
            height = file.ReadInt16();

            var buf = new byte[entry.Length];
            file.Read(buf);

            var data = new uint[width * height];

            fixed (byte* startPtr = buf)
            {
                ushort* lineoffsets = (ushort*)startPtr;
                byte* datastart = (byte*)startPtr + height * 2;
                int x = 0;
                int y = 0;
                var ptr = (ushort*)(datastart + lineoffsets[0] * 2);

                while (y < height)
                {
                    ushort xoffs = *ptr++;
                    ushort run = *ptr++;

                    if (xoffs + run >= 2048)
                    {
                        break;
                    }

                    if (xoffs + run != 0)
                    {
                        x += xoffs;
                        int pos = y * width + x;

                        for (int j = 0; j < run; ++j, ++pos)
                        {
                            ushort val = *ptr++;

                            if (val != 0)
                            {
                                data[pos] = HuesHelper.Color16To32(val) | 0xFF_00_00_00;
                            }
                        }

                        x += run;
                    }
                    else
                    {
                        x = 0;
                        ++y;
                        ptr = (ushort*)(datastart + lineoffsets[y] * 2);
                    }
                }
            }

            return data;
        }

        private static void AddBlackBorder(Span<uint> pixels, int width, int height)
        {
            for (int yy = 0; yy < height; yy++)
            {
                int startY = yy != 0 ? -1 : 0;
                int endY = yy + 1 < height ? 2 : 1;

                for (int xx = 0; xx < width; xx++)
                {
                    ref uint pixel = ref pixels[yy * width + xx];

                    if (pixel == 0)
                    {
                        continue;
                    }

                    int startX = xx != 0 ? -1 : 0;
                    int endX = xx + 1 < width ? 2 : 1;

                    for (int i = startY; i < endY; i++)
                    {
                        int currentY = yy + i;

                        for (int j = startX; j < endX; j++)
                        {
                            int currentX = xx + j;

                            ref uint currentPixel = ref pixels[currentY * width + currentX];

                            if (currentPixel == 0u)
                            {
                                pixel = 0xFF_00_00_00;
                            }
                        }
                    }
                }
            }
        }

        public ArtInfo GetArt(uint idx)
        {
            if (_hdFile != null)
            {
                var hdPixels = TryLoadHD(_hdFile, idx, out var hdW, out var hdH);
                if (hdPixels != null)
                {
                    return new ArtInfo()
                    {
                        Pixels = hdPixels,
                        Width = hdW,
                        Height = hdH,
                        LogicalWidth = (short)(hdW / HD_SCALE),
                        LogicalHeight = (short)(hdH / HD_SCALE),
                    };
                }
            }

            ref var entry = ref _file.GetValidRefEntry((int)idx);
            var loadLand = idx < 0x4000;
            var pixels = loadLand ?
                LoadLand(_file, in entry, out var width, out var height)
                :
                LoadArt(_file, in entry, out width, out height);

            return new ArtInfo()
            {
                Pixels = pixels,
                Width = width,
                Height = height,
                LogicalWidth = width,
                LogicalHeight = height,
            };
        }
    }

    public ref struct ArtInfo
    {
        public Span<uint> Pixels;
        public int Width;
        public int Height;
        public int LogicalWidth;
        public int LogicalHeight;
    }
}
