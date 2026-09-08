using DfoServer.Game.DailyReset;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Dungeon
{
    /// <summary>
    /// 暴走安徒恩 (243-247) 每日每副本"已领取怪物掉落"守卫。
    /// 通关时 TryMarkLootClaimed 写入 character_daily_counters，
    /// 怪物死亡时 HasClaimedLootToday 检查后跳过 GenerateAndRegister。
    /// 跨天 06:00 由 DailyResetService 自动归零。
    /// </summary>
    internal sealed class AntonAwakeningDailyLootGuard
    {
        private const string LootCounterKeyPrefix = "anton_awakening_loot_";

        internal static readonly int[] AntonAwakeningDungeonIds =
            { 243, 244, 245, 246, 247 };

        private readonly string _connectionString;
        private readonly DailyResetService _dailyReset;

        internal AntonAwakeningDailyLootGuard(
            string connectionString,
            DailyResetService dailyReset = null)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
            _dailyReset = dailyReset;
        }

        /// <summary>
        /// 标记今天该副本已通关领取怪物掉落（cap=1）。
        /// 返回 true 表示本次成功标记，false 表示今日已标记过或非 Anton_Awakening 副本。
        /// </summary>
        internal bool TryMarkLootClaimed(int characterId, int dungeonId)
        {
            if (!IsAntonAwakeningDungeon(dungeonId))
                return false;

            if (_dailyReset == null || characterId <= 0)
                return false;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var applied = _dailyReset.TryIncrementCounter(
                        conn,
                        tx,
                        characterId,
                        LootCounterKeyPrefix + dungeonId,
                        cap: 1,
                        period: DailyResetService.PeriodDay);
                    tx.Commit();
                    return applied;
                }
            }
        }

        /// <summary>
        /// 检查今天是否已经领过该副本的怪物掉落。
        /// 返回 true 表示"今日已领"，应跳过 GenerateAndRegister。
        /// </summary>
        internal bool HasClaimedLootToday(int characterId, int dungeonId)
        {
            if (!IsAntonAwakeningDungeon(dungeonId))
                return false;

            if (_dailyReset == null || characterId <= 0)
                return false;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    var count = _dailyReset.GetCounter(
                        conn,
                        tx,
                        characterId,
                        LootCounterKeyPrefix + dungeonId);
                    tx.Commit();
                    return count > 0;
                }
            }
        }

        internal static bool IsAntonAwakeningDungeon(int dungeonId)
            => Array.IndexOf(AntonAwakeningDungeonIds, dungeonId) >= 0;
    }
}
