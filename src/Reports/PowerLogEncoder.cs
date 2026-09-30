using System;
using System.IO;
using System.Text;
using SevenZip;

namespace NomisKitchen.Reports
{
    internal static class PowerLogEncoder
    {
        internal const string TextEntry = "power.text.lzma";
        internal const string TimesEntry = "power.time.lzma";
        internal const string Encoding = "lzma-split-v1";
        const int DictionarySize = 1 << 23;
        const int FastBytes = 32;
        const int PrefixLength = 19;

        internal static void Write(ReportZip zip, Action<Action<string>> forEachLine, Func<string, string> clean)
        {
            var textPath = Path.Combine(Path.GetTempPath(), "nomi-power-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var times = new MemoryStream();
                long previous = 0;
                using (var text = new StreamWriter(new FileStream(textPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16), new UTF8Encoding(false)))
                {
                    forEachLine(raw =>
                    {
                        var line = clean(raw);
                        if (TryTicks(line, out long ticks))
                        {
                            WriteVarint(times, ticks - previous);
                            previous = ticks;
                            text.Write('\u0001');
                            text.Write(line.Substring(PrefixLength));
                        }
                        else if (line.Length > 0 && (line[0] == '\u0001' || line[0] == '\u0002'))
                        {
                            text.Write('\u0002');
                            text.Write(line);
                        }
                        else
                        {
                            text.Write(line);
                        }
                        text.Write('\n');
                    });
                }
                using (var input = new FileStream(textPath, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16))
                    WriteLzma(zip, TextEntry, input);
                times.Position = 0;
                WriteLzma(zip, TimesEntry, times);
            }
            finally
            {
                try { File.Delete(textPath); } catch { }
            }
        }

        static bool TryTicks(string line, out long ticks)
        {
            ticks = 0;
            if (line.Length < PrefixLength || line[0] != 'D' || line[1] != ' ' || line[4] != ':' || line[7] != ':' || line[10] != '.' || line[18] != ' ')
                return false;
            if (!Digits(line, 2, 2, out long hours) || !Digits(line, 5, 2, out long minutes) || !Digits(line, 8, 2, out long seconds)
                || !Digits(line, 11, 7, out long fraction) || hours > 23 || minutes > 59 || seconds > 59)
                return false;
            ticks = ((hours * 60 + minutes) * 60 + seconds) * 10_000_000L + fraction;
            return true;
        }

        static bool Digits(string line, int start, int count, out long value)
        {
            value = 0;
            for (int i = start; i < start + count; i++)
            {
                char c = line[i];
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

        static void WriteLzma(ReportZip zip, string name, Stream input)
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
                encoder.Code(input, output, size, -1, null);
            }
        }
    }
}
