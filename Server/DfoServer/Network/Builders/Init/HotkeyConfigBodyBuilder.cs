using System;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Settings;

namespace DfoServer.Network.Builders
{
    public sealed class HotkeyConfigBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => 0x01C7;

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, int occurrenceIndex, out byte[] body)
        {
            var init = snapshot.InitializationSnapshot;
            var job = snapshot.CharacterRecord?.Job ?? 0;

            // 01C7 必须恒发: 客户端换角色不会自行重置内存键位表, 跳过会让无存档角色
            // (新建角色)直接沿用上一个选取角色的键位, 并在客户端下一次
            // SAVE_GAME_OPTION_2 时被固化为该角色自己的存档。
            // 缔造者键位体系不同, keyType 固定为 1(含已有存档的角色)。
            var keyType = CharacterKeyboardDefaults.ResolveKeyType(job, init.HotkeyKeyType);
            if (init.HotkeyConfigSlots.Count == 0)
            {
                body = AccountSettingsPacketBuilder.BuildHotkeyOptionBody(
                    keyType,
                    CharacterKeyboardDefaults.BuildHotkeySlots(job));
                return true;
            }

            body = AccountSettingsPacketBuilder.BuildHotkeyOptionBody(keyType, init.HotkeyConfigSlots);
            return true;
        }
    }
}
