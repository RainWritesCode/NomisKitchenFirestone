using System;
using System.IO;
using SevenZip;

namespace NomisKitchen.Reports
{
    internal delegate void ByteLine(byte[] line, int length);

    internal static class ByteText
    {
        internal static byte[] Ascii(string text) => System.Text.Encoding.ASCII.GetBytes(text);

        internal static int IndexOf(byte[] line, int length, byte value, int from = 0)
        {
            for (int i = from; i < length; i++)
                if (line[i] == value) return i;
            return -1;
        }

        internal static int IndexOf(byte[] line, int length, byte[] pattern, int from = 0)
        {
            int last = length - pattern.Length;
            byte first = pattern[0];
            for (int i = from; i <= last; i++)
            {
                if (line[i] != first) continue;
                if (StartsAt(line, length, i, pattern)) return i;
            }
            return -1;
        }

        internal static bool StartsAt(byte[] line, int length, int at, byte[] pattern)
        {
            if (at + pattern.Length > length) return false;
            for (int j = 0; j < pattern.Length; j++)
                if (line[at + j] != pattern[j]) return false;
            return true;
        }
    }

    internal static class PowerLogEncoder
    {
        internal const string TextEntry = "power.text.lzma";
        internal const string TimesEntry = "power.time.lzma";
        internal const string Encoding = "lzma-split-v1";
        static readonly int DictionarySize = Type.GetType("Mono.Runtime") != null ? 1 << 21 : 1 << 23;
        const int FastBytes = 32;
        const int PrefixLength = 19;

        internal static void Write(ReportZip zip, Action<ByteLine> forEachLine, Redactor redactor, ICodeProgress progress)
        {
            var textPath = Path.Combine(Path.GetTempPath(), "nomi-power-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var times = new MemoryStream();
                long previous = 0;
                using (var text = new FileStream(textPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    forEachLine((raw, rawLength) =>
                    {
                        byte[] line = raw;
                        int length = rawLength;
                        if (redactor.MayChange(raw, rawLength))
                        {
                            line = System.Text.Encoding.UTF8.GetBytes(redactor.CleanGameLine(System.Text.Encoding.UTF8.GetString(raw, 0, rawLength)));
                            length = line.Length;
                        }
                        if (TryTicks(line, length, out long ticks))
                        {
                            WriteVarint(times, ticks - previous);
                            previous = ticks;
                            text.WriteByte(1);
                            text.Write(line, PrefixLength, length - PrefixLength);
                        }
                        else if (length > 0 && (line[0] == 1 || line[0] == 2))
                        {
                            text.WriteByte(2);
                            text.Write(line, 0, length);
                        }
                        else
                        {
                            text.Write(line, 0, length);
                        }
                        text.WriteByte((byte)'\n');
                    });
                }
                using (var input = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16))
                    WriteLzma(zip, TextEntry, input, progress);
                times.Position = 0;
                WriteLzma(zip, TimesEntry, times, progress);
            }
            finally
            {
                try { File.Delete(textPath); } catch { }
            }
        }

        static bool TryTicks(byte[] line, int length, out long ticks)
        {
            ticks = 0;
            if (length < PrefixLength || line[0] != 'D' || line[1] != ' ' || line[4] != ':' || line[7] != ':' || line[10] != '.' || line[18] != ' ')
                return false;
            if (!Digits(line, 2, 2, out long hours) || !Digits(line, 5, 2, out long minutes) || !Digits(line, 8, 2, out long seconds)
                || !Digits(line, 11, 7, out long fraction) || hours > 23 || minutes > 59 || seconds > 59)
                return false;
            ticks = ((hours * 60 + minutes) * 60 + seconds) * 10_000_000L + fraction;
            return true;
        }

        static bool Digits(byte[] line, int start, int count, out long value)
        {
            value = 0;
            for (int i = start; i < start + count; i++)
            {
                byte c = line[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        static void WriteVarint(Stream output, long delta)
        {
            ulong zigzag = (ulong)((delta << 1) ^ (delta >> 63));
            do
            {
                byte b = (byte)(zigzag & 0x7f);
                zigzag >>= 7;
                output.WriteByte(zigzag != 0 ? (byte)(b | 0x80) : b);
            } while (zigzag != 0);
        }

        static void WriteLzma(ReportZip zip, string name, Stream input, ICodeProgress progress)
        {
            using (var output = zip.Create(name))
            {
                var encoder = new SevenZip.Compression.LZMA.Encoder();
                encoder.SetCoderProperties(
                    new[] { CoderPropID.DictionarySize, CoderPropID.NumFastBytes, CoderPropID.MatchFinder, CoderPropID.Algorithm },
                    new object[] { DictionarySize, FastBytes, "bt4", 2 });
                encoder.WriteCoderProperties(output);
                long size = input.Length;
                for (int i = 0; i < 8; i++) output.WriteByte((byte)(size >> (8 * i)));
                encoder.Code(input, output, size, -1, progress);
            }
        }
    }
}
