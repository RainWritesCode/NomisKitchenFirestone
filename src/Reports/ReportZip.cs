using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NomisKitchen.Reports
{
    internal sealed class ReportZip : IDisposable
    {
        static readonly uint[] CrcTable = BuildCrcTable();

        readonly Stream _output;
        readonly List<Stored> _entries = new List<Stored>();
        bool _finished;

        public ReportZip(Stream output)
        {
            _output = output;
        }

        public Stream Create(string name) => new Entry(this, name);

        public void Add(string name, byte[] data)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var stored = new Stored { Name = nameBytes, Crc = Crc32(data), Size = data.Length, Offset = _output.Position };
            var writer = new BinaryWriter(_output, Encoding.UTF8, true);
            writer.Write(0x04034b50u);
            writer.Write((ushort)20);
            writer.Write((ushort)0x0800);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0x21);
            writer.Write(stored.Crc);
            writer.Write(stored.Size);
            writer.Write(stored.Size);
            writer.Write((ushort)nameBytes.Length);
            writer.Write((ushort)0);
            writer.Write(nameBytes);
            writer.Flush();
            _output.Write(data, 0, data.Length);
            _entries.Add(stored);
        }

        public void Dispose()
        {
            if (_finished) return;
            _finished = true;
            long directory = _output.Position;
            var writer = new BinaryWriter(_output, Encoding.UTF8, true);
            foreach (var entry in _entries)
            {
                writer.Write(0x02014b50u);
                writer.Write((ushort)20);
                writer.Write((ushort)20);
                writer.Write((ushort)0x0800);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0x21);
                writer.Write(entry.Crc);
                writer.Write(entry.Size);
                writer.Write(entry.Size);
                writer.Write((ushort)entry.Name.Length);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write(0u);
                writer.Write((uint)entry.Offset);
                writer.Write(entry.Name);
            }
            long end = _output.Position;
            writer.Write(0x06054b50u);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)_entries.Count);
            writer.Write((ushort)_entries.Count);
            writer.Write((uint)(end - directory));
            writer.Write((uint)directory);
            writer.Write((ushort)0);
            writer.Flush();
        }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        sealed class Stored
        {
            public byte[] Name;
            public uint Crc;
            public int Size;
            public long Offset;
        }

        sealed class Entry : MemoryStream
        {
            readonly ReportZip _zip;
            readonly string _name;
            bool _closed;

            public Entry(ReportZip zip, string name)
            {
                _zip = zip;
                _name = name;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_closed)
                {
                    _closed = true;
                    _zip.Add(_name, ToArray());
                }
                base.Dispose(disposing);
            }
        }
    }
}
