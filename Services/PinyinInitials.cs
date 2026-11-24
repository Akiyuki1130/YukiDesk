using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace DesktopBar.Services
{
    internal static class PinyinInitials
    {
        private const int FirstHan = 0x3000;
        private const uint HanFlag = 1u << 31;
        private static readonly Lazy<uint[]> Readings = new(LoadReadings);

        internal static uint[] ParseTerm(string term)
        {
            var pattern = new uint[term.Length];
            for (int i = 0; i < term.Length; i++)
            {
                pattern[i] = LetterMask(term[i]);
                if (pattern[i] == 0) return null;
            }
            return pattern;
        }

        internal static bool Matches(string name, uint[] pattern)
        {
            if (pattern == null || pattern.Length == 0 || pattern.Length > name.Length) return false;
            Span<uint> positions = name.Length <= 256 ? stackalloc uint[name.Length] : new uint[name.Length];
            int count = 0;
            bool hasHan = false;
            foreach (var rune in name.EnumerateRunes())
            {
                uint mask = LetterMask(rune.Value);
                if (rune.Value >= FirstHan)
                {
                    var readings = Readings.Value;
                    int index = rune.Value - FirstHan;
                    if (index < readings.Length && readings[index] != 0)
                    {
                        mask = readings[index] | HanFlag;
                        hasHan = true;
                    }
                }
                if (mask == 0 && (Rune.IsWhiteSpace(rune) || Rune.IsPunctuation(rune))) continue;
                positions[count++] = mask;
            }
            if (!hasHan) return false;

            // Each name position can have several Mandarin readings. Compare their initials without
            // constructing every possible reading of the name.
            for (int start = 0; start <= count - pattern.Length; start++)
            {
                int offset = 0;
                uint matched = 0;
                while (offset < pattern.Length && (positions[start + offset] & pattern[offset]) != 0)
                    matched |= positions[start + offset++];
                if (offset == pattern.Length && (matched & HanFlag) != 0) return true;
            }
            return false;
        }

        private static uint LetterMask(int value)
        {
            int index = (value | 32) - 'a';
            return (uint)index < 26 ? 1u << index : 0;
        }

        private static uint[] LoadReadings()
        {
            using var stream = typeof(PinyinInitials).Assembly.GetManifestResourceStream("DesktopBar.UnihanPinyinInitials");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            int largest = int.Parse(reader.ReadLine(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var readings = new uint[largest - FirstHan + 1];
            string row;
            while ((row = reader.ReadLine()) != null)
            {
                uint mask = LetterMask(row[0]);
                foreach (var rune in row.AsSpan(1).EnumerateRunes()) readings[rune.Value - FirstHan] |= mask;
            }
            return readings;
        }
    }
}
