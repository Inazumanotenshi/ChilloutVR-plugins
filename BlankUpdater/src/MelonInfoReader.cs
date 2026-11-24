using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace BlankUpdater
{
    /// <summary>
    /// Reads the MelonInfo attribute (name, version, author) straight from a DLL file
    /// without loading it. Works the same way as manifest.php on the server.
    /// </summary>
    internal static class MelonInfoReader
    {
        internal sealed class Info
        {
            public string Name;
            public string Version;
            public string Author;
        }

        private static readonly Regex VersionPattern = new Regex(@"^\d+(\.\d+){1,3}([-+][0-9A-Za-z.+-]+)?$", RegexOptions.Compiled);

        public static Info Read(string path)
        {
            try
            {
                return Read(File.ReadAllBytes(path));
            }
            catch
            {
                return null;
            }
        }

        public static Info Read(byte[] data)
        {
            try
            {
                return Parse(data);
            }
            catch
            {
                return null;
            }
        }

        private static Info Parse(byte[] data)
        {
            int length = data.Length;
            if (length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z')
                return null;

            int pe = (int)U32(data, 0x3C);
            if (pe < 0 || pe + 24 > length || data[pe] != (byte)'P' || data[pe + 1] != (byte)'E' || data[pe + 2] != 0 || data[pe + 3] != 0)
                return null;

            int sectionCount = U16(data, pe + 6);
            int optionalSize = U16(data, pe + 20);
            int optional = pe + 24;
            int magic = U16(data, optional);
            int dataDirectories = optional + (magic == 0x20B ? 112 : 96);
            uint cliRva = U32(data, dataDirectories + 14 * 8);
            if (cliRva == 0)
                return null;

            var sections = new List<(uint va, uint size, uint raw)>();
            int sectionTable = optional + optionalSize;
            for (int i = 0; i < sectionCount; i++)
            {
                int s = sectionTable + i * 40;
                sections.Add((U32(data, s + 12), Math.Max(U32(data, s + 8), U32(data, s + 16)), U32(data, s + 20)));
            }

            int ToOffset(uint rva)
            {
                foreach (var (va, size, raw) in sections)
                {
                    if (rva >= va && rva < va + size)
                        return (int)(rva - va + raw);
                }
                return -1;
            }

            int cli = ToOffset(cliRva);
            if (cli < 0)
                return null;
            int meta = ToOffset(U32(data, cli + 8));
            if (meta < 0 || U32(data, meta) != 0x424A5342)
                return null;

            int versionLength = (int)U32(data, meta + 12);
            int pos = meta + 16 + versionLength + 2;
            int streamCount = U16(data, pos);
            pos += 2;

            int blobStart = -1;
            int blobSize = 0;
            for (int i = 0; i < streamCount; i++)
            {
                int offset = (int)U32(data, pos);
                int size = (int)U32(data, pos + 4);
                int nameStart = pos + 8;
                int nameEnd = nameStart;
                while (nameEnd < length && data[nameEnd] != 0)
                    nameEnd++;
                string name = Encoding.ASCII.GetString(data, nameStart, nameEnd - nameStart);
                pos = nameStart + ((name.Length + 4) / 4) * 4;
                if (name == "#Blob")
                {
                    blobStart = meta + offset;
                    blobSize = size;
                }
            }
            if (blobStart < 0)
                return null;

            int p = blobStart + 1;
            int heapEnd = Math.Min(blobStart + blobSize, length);
            while (p < heapEnd)
            {
                int size = ReadLength(data, ref p);
                int blobEnd = p + size;
                if (blobEnd > heapEnd)
                    break;

                // MelonInfo(Type, name, version, author, [downloadLink]) - all arguments are serialized as strings
                if (size >= 6 && data[p] == 0x01 && data[p + 1] == 0x00)
                {
                    int q = p + 2;
                    var strings = new List<string>();
                    while (q < blobEnd && strings.Count < 5)
                    {
                        if (data[q] == 0xFF)
                        {
                            strings.Add(null);
                            q++;
                            continue;
                        }
                        int stringLength = ReadLength(data, ref q);
                        if (q + stringLength > blobEnd)
                            break;
                        strings.Add(Encoding.UTF8.GetString(data, q, stringLength));
                        q += stringLength;
                    }

                    if (strings.Count >= 4 && !string.IsNullOrEmpty(strings[1]) && strings[2] != null && VersionPattern.IsMatch(strings[2]))
                        return new Info { Name = strings[1], Version = strings[2], Author = strings[3] };
                }
                p = blobEnd;
            }

            return null;
        }

        private static int ReadLength(byte[] data, ref int p)
        {
            int b = data[p];
            if ((b & 0x80) == 0)
            {
                p += 1;
                return b;
            }
            if ((b & 0xC0) == 0x80)
            {
                int v = ((b & 0x3F) << 8) | data[p + 1];
                p += 2;
                return v;
            }
            int w = ((b & 0x1F) << 24) | (data[p + 1] << 16) | (data[p + 2] << 8) | data[p + 3];
            p += 4;
            return w;
        }

        private static int U16(byte[] data, int offset)
        {
            if (offset < 0 || offset + 2 > data.Length)
                return 0;
            return data[offset] | (data[offset + 1] << 8);
        }

        private static uint U32(byte[] data, int offset)
        {
            if (offset < 0 || offset + 4 > data.Length)
                return 0;
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }
}
