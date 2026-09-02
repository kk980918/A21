using System;
using System.Collections.Generic;

namespace PvfLib
{
    // Packer-only. Walks existing Type1 tokens and replaces ability percents
    // in place. Does not decode or re-tokenize, so float slots stay float.
    public static class PvfType1AbilityPatch
    {
        public const int MinPercent = 1;
        public const int MaxPercent = 100000;

        public static bool TryReplacePercent(
            byte[] raw,
            Func<int, string> resolveString,
            string statName,
            int newPercent,
            out byte[] patched,
            out string error)
        {
            return TryReplacePercents(
                raw,
                resolveString,
                new Dictionary<string, int> { [statName ?? string.Empty] = newPercent },
                out patched,
                out error);
        }

        public static bool TryReplacePercents(
            byte[] raw,
            Func<int, string> resolveString,
            IReadOnlyDictionary<string, int> percents,
            out byte[] patched,
            out string error)
        {
            patched = null;
            error = null;

            if (raw == null || raw.Length < 15 || raw.Length % 5 != 0)
            {
                error = "Type1 payload is missing or not 5-byte aligned";
                return false;
            }

            if (resolveString == null)
            {
                error = "string resolver is required";
                return false;
            }

            if (percents == null || percents.Count == 0)
            {
                error = "at least one ability percent is required";
                return false;
            }

            var requested = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in percents)
            {
                var want = NormalizeStatName(pair.Key);
                if (string.IsNullOrEmpty(want))
                {
                    error = "stat name is empty";
                    return false;
                }

                if (pair.Value < MinPercent || pair.Value > MaxPercent)
                {
                    error = $"percent {pair.Value} is outside {MinPercent}..{MaxPercent}";
                    return false;
                }

                if (requested.ContainsKey(want))
                {
                    error = $"stat {want} was requested more than once";
                    return false;
                }

                requested[want] = pair.Value;
            }

            var matchOffset = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tokens = raw.Length / 5;
            for (var i = 0; i + 2 < tokens; i++)
            {
                if (raw[i * 5] != 6 || raw[(i + 1) * 5] != 6)
                    continue;

                var name = NormalizeStatName(
                    resolveString(BitConverter.ToInt32(raw, i * 5 + 1)));
                if (!requested.ContainsKey(name))
                    continue;

                var op = resolveString(BitConverter.ToInt32(raw, (i + 1) * 5 + 1));
                if (!string.Equals(op, "*", StringComparison.Ordinal))
                    continue;

                var valueType = raw[(i + 2) * 5];
                if (valueType == 2)
                {
                    error = $"stat {name} `*` is followed by a float slot";
                    return false;
                }

                if (valueType != 0)
                    continue;

                if (matchOffset.ContainsKey(name))
                {
                    error = $"stat {name} matched more than once";
                    return false;
                }

                matchOffset[name] = (i + 2) * 5 + 1;
            }

            foreach (var name in requested.Keys)
            {
                if (matchOffset.ContainsKey(name))
                    continue;
                error = $"stat {name} `*` integer triple was not found";
                return false;
            }

            patched = (byte[])raw.Clone();
            foreach (var pair in requested)
                WriteInt32LittleEndian(patched, matchOffset[pair.Key], pair.Value);
            return true;
        }

        public static bool TryReplacePercent(
            PvfArchive archive,
            string relativePath,
            string statName,
            int newPercent,
            out string error)
        {
            return TryReplacePercents(
                archive,
                relativePath,
                new Dictionary<string, int> { [statName ?? string.Empty] = newPercent },
                out error);
        }

        public static bool TryReplacePercents(
            PvfArchive archive,
            string relativePath,
            IReadOnlyDictionary<string, int> percents,
            out string error)
        {
            error = null;
            if (archive == null)
            {
                error = "archive is required";
                return false;
            }

            var index = archive.FindFileIndex(relativePath);
            if (index < 0)
            {
                error = "PVF file not found: " + relativePath;
                return false;
            }

            if (archive.Files[index].Entry.DataType != 1)
            {
                error = "file is not Type1: " + relativePath;
                return false;
            }

            var raw = archive.GetFileRawData(index);
            if (!TryReplacePercents(raw, archive.ResolveString, percents, out var patched, out error))
                return false;

            archive.SetFileRawData(index, patched);
            return true;
        }

        private static void WriteInt32LittleEndian(byte[] dest, int offset, int value)
        {
            dest[offset] = (byte)value;
            dest[offset + 1] = (byte)(value >> 8);
            dest[offset + 2] = (byte)(value >> 16);
            dest[offset + 3] = (byte)(value >> 24);
        }

        private static string NormalizeStatName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            name = name.Trim();
            if (name.Length >= 2 && name[0] == '[' && name[name.Length - 1] == ']')
                return name;
            return "[" + name + "]";
        }
    }
}
