using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;

namespace DfoServer.Game.Settings
{
    // 无已保存键位(新建角色)时 01C7 使用的默认键位投影。
    // 普通职业(含黑暗武士)使用项目原始发布批次(15e536e)保留下来的默认槽位表:
    // 104 槽 DirectInput 键码, 0x86=未分配, 与缔造者解析的 UnassignedKey 语义一致。
    // 缔造者(job=10)默认布局来自 PVF clientonly/hotkeysystemforcreator.co。
    // 只做 init 投影, 不在创角时写库; 玩家保存后以 character_hotkey_slots 为准。
    public static class CharacterKeyboardDefaults
    {
        private const byte CreatorMageJob = 10;
        private const ushort UnassignedKey = 0x86;

        private static readonly Lazy<byte[]> CreatorHotkeys =
            new Lazy<byte[]>(BuildCreatorHotkeySlots);

        public static bool IsCreatorMage(byte job)
            => job == CreatorMageJob;

        // 缔造者键位体系与普通职业不同, keyType 固定为 1; 其余跟随账号设置。
        public static byte ResolveKeyType(byte job, byte accountKeyType)
            => IsCreatorMage(job) ? (byte)1 : accountKeyType;

        public static byte[] BuildHotkeySlots(byte job)
            => Clone(IsCreatorMage(job) ? CreatorHotkeys.Value : DefaultHotkeySlots);

        private static byte[] BuildCreatorHotkeySlots()
        {
            try
            {
                var values = ParseDefaultKeys(PvfArchiveAccessor.ReadText("clientonly/hotkeysystemforcreator.co"));
                if (values.Count == 0)
                    return Clone(DefaultHotkeySlots);

                var headerSlots = 4;
                var result = new byte[(headerSlots + values.Count) * 2];
                Buffer.BlockCopy(DefaultHotkeySlots, 0, result, 0, headerSlots * 2);
                for (var i = 0; i < values.Count; i++)
                    Buffer.BlockCopy(BitConverter.GetBytes(values[i]), 0, result, (headerSlots + i) * 2, 2);
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[CharacterKeyboardDefaults] creator hotkey parse failed: {ex.Message}");
                return Clone(DefaultHotkeySlots);
            }
        }

        private static List<ushort> ParseDefaultKeys(string text)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (Match keyBlock in Regex.Matches(text, @"\[key\]\s+`[^`]*`\s+-?\d+\s+`[^`]*`\s+`[^`]*`\s+(-?\d+)", RegexOptions.IgnoreCase))
            {
                if (!int.TryParse(keyBlock.Groups[1].Value, out var value))
                    continue;
                result.Add(value < 0 ? UnassignedKey : (ushort)Math.Min(ushort.MaxValue, value));
            }

            return result;
        }

        private static byte[] Clone(byte[] source)
        {
            if (source == null)
                return Array.Empty<byte>();
            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        public static readonly byte[] DefaultHotkeySlots = {
            0x02,0x00,0x00,0x00,0x03,0x00,0x01,0x00,0x4E,0x00,0x39,0x00,0x50,0x00,0x0D,0x00,
            0x4C,0x00,0x43,0x00,0x41,0x00,0x3F,0x00,0x45,0x00,0x52,0x00,0x4B,0x00,0x55,0x00,
            0x44,0x00,0x56,0x00,0x54,0x00,0x51,0x00,0x37,0x00,0x49,0x00,0x3A,0x00,0x3C,0x00,
            0x3D,0x00,0x3E,0x00,0x47,0x00,0x4D,0x00,0x3B,0x00,0x48,0x00,0x4A,0x00,0x4F,0x00,
            0x2E,0x00,0x2F,0x00,0x30,0x00,0x31,0x00,0x32,0x00,0x33,0x00,0x86,0x00,0x1C,0x00,
            0x53,0x00,0x57,0x00,0x86,0x00,0x6C,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,
            0x1A,0x00,0x46,0x00,0x68,0x00,0x69,0x00,0x42,0x00,0x1B,0x00,0x6A,0x00,0x86,0x00,
            0x86,0x00,0x86,0x00,0x38,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,
            0x86,0x00,0x86,0x00,0x07,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,
            0x86,0x00,0x86,0x00,0x59,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x58,0x00,
            0x86,0x00,0x5A,0x00,0x5B,0x00,0x5C,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,
            0x1F,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x86,0x00,0x15,0x00,
            0x86,0x00,0x18,0x00,0x86,0x00
        };
    }
}
