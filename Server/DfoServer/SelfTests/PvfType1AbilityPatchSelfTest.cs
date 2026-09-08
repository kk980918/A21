using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;

namespace DfoServer.SelfTests
{
    public static class PvfType1AbilityPatchSelfTest
    {
        private const string TauArmyPath = "monster/Tau/TauArmy.mob";
        private const string TauGuardPath = "monster/Tau/TauGuard.mob";

        public static int Run()
        {
            Console.WriteLine("=== PVF_TYPE1_ABILITY_PATCH selftest ===");
            var failures = 0;

            VerifyReplacesOnlyTheInteger(ref failures);
            VerifyPatchesNeighborAttack(ref failures);
            VerifyBatchReplacesSeveralPercents(ref failures);
            VerifyBatchRefusesMissingKeyWithoutWrite(ref failures);
            VerifyRejectsFloatSlot(ref failures);
            VerifyRejectsDuplicateStat(ref failures);
            VerifyRejectsOutOfRange(ref failures);
            VerifyRejectsUnalignedPayload(ref failures);
            VerifyPatchesWarlikeTaggedInt(ref failures);
            VerifyLiveTauArmy(ref failures);
            VerifyLiveTauGuardWarlike(ref failures);

            Console.WriteLine(failures == 0
                ? "PVF_TYPE1_ABILITY_PATCH selftest passed"
                : $"PVF_TYPE1_ABILITY_PATCH selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyReplacesOnlyTheInteger(ref int failures)
        {
            var strings = AbilityStrings();
            var raw = OfficialAbilityPayload();

            Check(
                "patches HP MAX 130 to 200",
                PvfType1AbilityPatch.TryReplacePercent(
                    raw, id => strings[id], "[HP MAX]", 200, out var patched, out var error)
                    && error == null
                    && patched.Length == raw.Length
                    && patched[10] == 0
                    && patched[11] == 0xC8
                    && patched[12] == 0
                    && patched[13] == 0
                    && patched[14] == 0
                    && ReadInt(patched, 11) == 200
                    && ReadInt(patched, 26) == 100
                    && ReadInt(patched, 41) == 100
                    && ReadInt(patched, 56) == 70
                    && ReadInt(patched, 71) == 100
                    && patched[75] == 2
                    && ReadInt(patched, 76) == FloatBits(30f)
                    && TypesEqual(raw, patched)
                    && ReadInt(raw, 11) == 130
                    && raw[11] == 0x82,
                ref failures);
        }

        private static void VerifyPatchesNeighborAttack(ref int failures)
        {
            var strings = AbilityStrings();
            var raw = OfficialAbilityPayload();
            Check(
                "patches magical attack 70 to 80 and leaves HP 130",
                PvfType1AbilityPatch.TryReplacePercent(
                    raw,
                    id => strings[id],
                    "EQUIPMENT_MAGICAL_ATTACK",
                    80,
                    out var patched,
                    out var error)
                    && error == null
                    && TypesEqual(raw, patched)
                    && CountChangedTokens(raw, patched) == 1
                    && ReadInt(patched, 11) == 130
                    && ReadInt(patched, 56) == 80
                    && patched[55] == 0
                    && patched[75] == 2,
                ref failures);
        }

        private static void VerifyBatchReplacesSeveralPercents(ref int failures)
        {
            var strings = AbilityStrings();
            var raw = OfficialAbilityPayload();
            var percents = new Dictionary<string, int>
            {
                ["[HP MAX]"] = 200,
                ["[EQUIPMENT_PHYSICAL_ATTACK]"] = 150,
                ["[EQUIPMENT_PHYSICAL_DEFENSE]"] = 150,
                ["[EQUIPMENT_MAGICAL_ATTACK]"] = 80,
                ["[EQUIPMENT_MAGICAL_DEFENSE]"] = 150,
            };
            Check(
                "batch-patches all five ability percents",
                PvfType1AbilityPatch.TryReplacePercents(
                    raw, id => strings[id], percents, out var patched, out var error)
                    && error == null
                    && TypesEqual(raw, patched)
                    && CountChangedTokens(raw, patched) == 5
                    && ReadInt(patched, 11) == 200
                    && ReadInt(patched, 26) == 150
                    && ReadInt(patched, 41) == 150
                    && ReadInt(patched, 56) == 80
                    && ReadInt(patched, 71) == 150
                    && patched[75] == 2
                    && ReadInt(patched, 76) == FloatBits(30f)
                    && ReadInt(raw, 11) == 130,
                ref failures);
        }

        private static void VerifyBatchRefusesMissingKeyWithoutWrite(ref int failures)
        {
            var strings = AbilityStrings();
            var raw = OfficialAbilityPayload();
            var percents = new Dictionary<string, int>
            {
                ["[HP MAX]"] = 200,
                ["[HP MAX BOSS]"] = 200,
            };
            Check(
                "batch refuses a missing key and does not write HP",
                !PvfType1AbilityPatch.TryReplacePercents(
                    raw, id => strings[id], percents, out var patched, out var error)
                    && patched == null
                    && error != null
                    && error.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                    && ReadInt(raw, 11) == 130,
                ref failures);
        }

        private static void VerifyRejectsFloatSlot(ref int failures)
        {
            var strings = new Dictionary<int, string> { [1] = "[HP MAX]", [2] = "*" };
            var raw = Concat(Token(6, 1), Token(6, 2), Token(2, FloatBits(130f)));
            Check(
                "refuses to overwrite a float slot",
                !PvfType1AbilityPatch.TryReplacePercent(
                    raw, id => strings[id], "HP MAX", 200, out var patched, out var error)
                    && patched == null
                    && error != null
                    && error.IndexOf("float", StringComparison.OrdinalIgnoreCase) >= 0,
                ref failures);
        }

        private static void VerifyRejectsDuplicateStat(ref int failures)
        {
            var strings = new Dictionary<int, string> { [1] = "[HP MAX]", [2] = "*" };
            var raw = Concat(
                Token(6, 1), Token(6, 2), Token(0, 130),
                Token(6, 1), Token(6, 2), Token(0, 140));
            Check(
                "refuses an ambiguous second HP MAX triple",
                !PvfType1AbilityPatch.TryReplacePercent(
                    raw, id => strings[id], "[HP MAX]", 200, out _, out var error)
                    && error != null
                    && error.IndexOf("more than once", StringComparison.Ordinal) >= 0,
                ref failures);
        }

        private static void VerifyRejectsOutOfRange(ref int failures)
        {
            var strings = new Dictionary<int, string> { [1] = "[HP MAX]", [2] = "*" };
            var raw = Concat(Token(6, 1), Token(6, 2), Token(0, 130));
            Check(
                "rejects 0 percent",
                !PvfType1AbilityPatch.TryReplacePercent(
                    raw, id => strings[id], "[HP MAX]", 0, out _, out _),
                ref failures);
        }

        private static void VerifyRejectsUnalignedPayload(ref int failures)
        {
            var strings = new Dictionary<int, string> { [1] = "[HP MAX]", [2] = "*" };
            var raw = Concat(Token(6, 1), Token(6, 2), Token(0, 130), new byte[] { 0x00 });
            Check(
                "rejects a payload that is not 5-byte aligned",
                !PvfType1AbilityPatch.TryReplacePercent(
                    raw, id => strings[id], "[HP MAX]", 200, out var patched, out var error)
                    && patched == null
                    && error != null
                    && error.IndexOf("aligned", StringComparison.OrdinalIgnoreCase) >= 0,
                ref failures);
        }

        private static void VerifyPatchesWarlikeTaggedInt(ref int failures)
        {
            var strings = new Dictionary<int, string> { [1] = "[warlike]" };
            var raw = Concat(Token(3, 1), Token(0, 70), Token(2, FloatBits(30f)));
            Check(
                "patches warlike 70 to 80 and leaves the following float",
                PvfType1AbilityPatch.TryReplaceTaggedInt(
                    raw, id => strings[id], "warlike", 80, out var patched, out var error)
                    && error == null
                    && TypesEqual(raw, patched)
                    && CountChangedTokens(raw, patched) == 1
                    && ReadInt(patched, 6) == 80
                    && patched[5] == 0
                    && patched[10] == 2,
                ref failures);
        }

        private static void VerifyLiveTauArmy(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("live TauArmy patch skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                using var archive = PvfArchive.Open(pvfPath);
                var index = archive.FindFileIndex(TauArmyPath);
                var raw = index >= 0 ? archive.GetFileRawData(index) : null;
                Check("live TauArmy raw exists", raw != null && raw.Length >= 15, ref failures);
                if (raw == null)
                    return;

                Check(
                    "live TauArmy has 182 Type1 slots",
                    raw.Length == 182 * 5,
                    ref failures);
                Check(
                    "live TauArmy [width] is 60 20",
                    HasConsecutiveInts(raw, 60, 20),
                    ref failures);
                Check(
                    "live TauArmy official ability percents",
                    FindAbilityInt(raw, archive.ResolveString, "[HP MAX]") == 130
                        && FindAbilityInt(raw, archive.ResolveString, "[EQUIPMENT_PHYSICAL_ATTACK]") == 100
                        && FindAbilityInt(raw, archive.ResolveString, "[EQUIPMENT_PHYSICAL_DEFENSE]") == 100
                        && FindAbilityInt(raw, archive.ResolveString, "[EQUIPMENT_MAGICAL_ATTACK]") == 70
                        && FindAbilityInt(raw, archive.ResolveString, "[EQUIPMENT_MAGICAL_DEFENSE]") == 100,
                    ref failures);

                Check(
                    "live TauArmy mag attack 70 -> 80 keeps HP 130",
                    PvfType1AbilityPatch.TryReplacePercent(
                        raw,
                        archive.ResolveString,
                        "[EQUIPMENT_MAGICAL_ATTACK]",
                        80,
                        out var magPatched,
                        out var magError)
                        && magError == null
                        && TypesEqual(raw, magPatched)
                        && CountChangedTokens(raw, magPatched) == 1
                        && FindAbilityInt(magPatched, archive.ResolveString, "[HP MAX]") == 130
                        && FindAbilityInt(magPatched, archive.ResolveString, "[EQUIPMENT_MAGICAL_ATTACK]") == 80,
                    ref failures);

                var percents = new Dictionary<string, int>
                {
                    ["[HP MAX]"] = 200,
                    ["[EQUIPMENT_PHYSICAL_ATTACK]"] = 150,
                    ["[EQUIPMENT_PHYSICAL_DEFENSE]"] = 150,
                    ["[EQUIPMENT_MAGICAL_ATTACK]"] = 80,
                    ["[EQUIPMENT_MAGICAL_DEFENSE]"] = 150,
                };
                Check(
                    "live TauArmy batch write-back keeps types",
                    PvfType1AbilityPatch.TryReplacePercents(
                        archive, TauArmyPath, percents, out var error)
                        && error == null,
                    ref failures);

                var patched = archive.GetFileRawData(index);
                var hpOffset = AbilityValueOffset(raw, archive.ResolveString, "[HP MAX]");
                Check(
                    "live TauArmy batch overlay changes five integer percents",
                    patched != null
                        && patched.Length == raw.Length
                        && TypesEqual(raw, patched)
                        && CountChangedTokens(raw, patched) == 5
                        && FindAbilityInt(patched, archive.ResolveString, "[HP MAX]") == 200
                        && FindAbilityInt(patched, archive.ResolveString, "[EQUIPMENT_PHYSICAL_ATTACK]") == 150
                        && FindAbilityInt(patched, archive.ResolveString, "[EQUIPMENT_PHYSICAL_DEFENSE]") == 150
                        && FindAbilityInt(patched, archive.ResolveString, "[EQUIPMENT_MAGICAL_ATTACK]") == 80
                        && FindAbilityInt(patched, archive.ResolveString, "[EQUIPMENT_MAGICAL_DEFENSE]") == 150
                        && hpOffset >= 0
                        && patched[hpOffset] == 0xC8
                        && patched[hpOffset + 1] == 0
                        && patched[hpOffset + 2] == 0
                        && patched[hpOffset + 3] == 0,
                    ref failures);

                archive.RevertFile(index);
                Check(
                    "live TauArmy failed batch does not write overlay",
                    !PvfType1AbilityPatch.TryReplacePercents(
                        archive,
                        TauArmyPath,
                        new Dictionary<string, int>
                        {
                            ["[HP MAX]"] = 200,
                            ["[HP MAX BOSS]"] = 200,
                        },
                        out _)
                        && !archive.IsFileModified(index)
                        && ReadInt(archive.GetFileRawData(index), hpOffset) == 130,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("live TauArmy patch failed: " + ex.Message);
                failures++;
            }
        }

        private static void VerifyLiveTauGuardWarlike(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("live TauGuard warlike skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            try
            {
                using var archive = PvfArchive.Open(pvfPath);
                var index = archive.FindFileIndex(TauGuardPath);
                var raw = index >= 0 ? archive.GetFileRawData(index) : null;
                Check("live TauGuard raw exists", raw != null && raw.Length >= 10, ref failures);
                if (raw == null)
                    return;

                Check(
                    "live TauGuard official warlike is 70",
                    FindTaggedInt(raw, archive.ResolveString, "[warlike]") == 70,
                    ref failures);

                Check(
                    "live TauGuard warlike 70 -> 80 keeps types",
                    PvfType1AbilityPatch.TryReplaceTaggedInt(
                        archive, TauGuardPath, "warlike", 80, out var error)
                        && error == null
                        && TypesEqual(raw, archive.GetFileRawData(index))
                        && CountChangedTokens(raw, archive.GetFileRawData(index)) == 1
                        && FindTaggedInt(
                            archive.GetFileRawData(index), archive.ResolveString, "[warlike]") == 80
                        && FindAbilityInt(
                            archive.GetFileRawData(index), archive.ResolveString, "[HP MAX]")
                            == FindAbilityInt(raw, archive.ResolveString, "[HP MAX]"),
                    ref failures);

                archive.RevertFile(index);
                Check(
                    "live TauGuard revert restores warlike 70",
                    FindTaggedInt(
                        archive.GetFileRawData(index), archive.ResolveString, "[warlike]") == 70
                        && !archive.IsFileModified(index),
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("live TauGuard warlike failed: " + ex.Message);
                failures++;
            }
        }

        private static Dictionary<int, string> AbilityStrings()
        {
            return new Dictionary<int, string>
            {
                [1] = "[HP MAX]",
                [2] = "*",
                [3] = "[EQUIPMENT_PHYSICAL_ATTACK]",
                [4] = "[EQUIPMENT_PHYSICAL_DEFENSE]",
                [5] = "[EQUIPMENT_MAGICAL_ATTACK]",
                [6] = "[EQUIPMENT_MAGICAL_DEFENSE]",
            };
        }

        private static byte[] OfficialAbilityPayload()
        {
            return Concat(
                Token(6, 1), Token(6, 2), Token(0, 130),
                Token(6, 3), Token(6, 2), Token(0, 100),
                Token(6, 4), Token(6, 2), Token(0, 100),
                Token(6, 5), Token(6, 2), Token(0, 70),
                Token(6, 6), Token(6, 2), Token(0, 100),
                Token(2, FloatBits(30f)));
        }

        private static int FindAbilityInt(
            byte[] raw,
            Func<int, string> resolve,
            string statName)
        {
            var offset = AbilityValueOffset(raw, resolve, statName);
            return offset >= 0 ? ReadInt(raw, offset) : int.MinValue;
        }

        private static int AbilityValueOffset(
            byte[] raw,
            Func<int, string> resolve,
            string statName)
        {
            var want = NormalizeStatName(statName);
            var tokens = raw.Length / 5;
            for (var i = 0; i + 2 < tokens; i++)
            {
                if (raw[i * 5] != 6 || raw[(i + 1) * 5] != 6 || raw[(i + 2) * 5] != 0)
                    continue;
                var name = NormalizeStatName(resolve(BitConverter.ToInt32(raw, i * 5 + 1)));
                var op = resolve(BitConverter.ToInt32(raw, (i + 1) * 5 + 1));
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(op, "*", StringComparison.Ordinal))
                    return (i + 2) * 5 + 1;
            }
            return -1;
        }

        private static int FindTaggedInt(
            byte[] raw,
            Func<int, string> resolve,
            string tagName)
        {
            var want = NormalizeStatName(tagName);
            var tokens = raw.Length / 5;
            for (var i = 0; i + 1 < tokens; i++)
            {
                if (raw[i * 5] != 3 || raw[(i + 1) * 5] != 0)
                    continue;
                var name = NormalizeStatName(resolve(BitConverter.ToInt32(raw, i * 5 + 1)));
                if (string.Equals(name, want, StringComparison.OrdinalIgnoreCase))
                    return ReadInt(raw, (i + 1) * 5 + 1);
            }
            return int.MinValue;
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

        private static bool HasConsecutiveInts(byte[] raw, int first, int second)
        {
            var tokens = raw.Length / 5;
            for (var i = 0; i + 1 < tokens; i++)
            {
                if (raw[i * 5] != 0 || raw[(i + 1) * 5] != 0)
                    continue;
                if (ReadInt(raw, i * 5 + 1) == first
                    && ReadInt(raw, (i + 1) * 5 + 1) == second)
                    return true;
            }
            return false;
        }

        private static int CountChangedTokens(byte[] left, byte[] right)
        {
            var changed = 0;
            var n = Math.Min(left.Length, right.Length) / 5;
            for (var i = 0; i < n; i++)
            {
                if (left[i * 5] != right[i * 5]
                    || ReadInt(left, i * 5 + 1) != ReadInt(right, i * 5 + 1))
                    changed++;
            }
            return changed;
        }

        private static bool TypesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var i = 0; i < left.Length; i += 5)
            {
                if (left[i] != right[i])
                    return false;
            }
            return true;
        }

        private static int ReadInt(byte[] raw, int offset)
        {
            return BitConverter.ToInt32(raw, offset);
        }

        private static byte[] Token(byte type, int value)
        {
            var raw = new byte[5];
            raw[0] = type;
            Buffer.BlockCopy(BitConverter.GetBytes(value), 0, raw, 1, 4);
            return raw;
        }

        private static int FloatBits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var size = 0;
            foreach (var part in parts)
                size += part.Length;
            var raw = new byte[size];
            var offset = 0;
            foreach (var part in parts)
            {
                Buffer.BlockCopy(part, 0, raw, offset, part.Length);
                offset += part.Length;
            }
            return raw;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            if (condition)
            {
                Console.WriteLine("PASS: " + name);
                return;
            }

            Console.WriteLine("FAIL: " + name);
            failures++;
        }
    }
}
