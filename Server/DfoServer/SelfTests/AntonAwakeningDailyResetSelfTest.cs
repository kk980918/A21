using DfoServer.Game.CharacterData;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DfoServer.SelfTests
{
    /// <summary>
    /// Anton_Awakening 每日重置行为测试：
    /// 1) 通关黑色火山 (247) 后 243-247 全部被锁住（DELETE + limit 扣减）
    /// 2) 跨天 06:00 重置只清空 Anton_Awakening 行，不影响其他副本
    /// 3) 247 每日首次通关触发 4 张特殊翻牌（squad_item + pcroom_card 池）
    /// </summary>
    public static class AntonAwakeningDailyResetSelfTest
    {
        // 抽卡池（灭杀安徒恩段 squad_item 5 个 + pcroom_card 1 个）
        private static readonly HashSet<uint> CardPoolItemIds = new HashSet<uint>
        {
            10157782, 10157783, 10157784, 10157785, 10157786, 10094733,
        };

        public static int Run()
        {
            Console.WriteLine("=== ANTON_AWAKENING_DAILY_RESET selftest ===");
            var failures = 0;
            VerifyClearWritesLootCounter(ref failures);
            VerifyCrossDayResetClearsOnlyAntonAwakening(ref failures);
            VerifyClearLeavesOtherDungeonsIntact(ref failures);
            VerifySpecialCardDrawnOnce(ref failures);
            VerifyDrawCardDistribution(ref failures);
            VerifyCrossDayResetsCardCounter(ref failures);
            Console.WriteLine(
                failures == 0
                    ? "ANTON_AWAKENING_DAILY_RESET selftest passed."
                    : $"ANTON_AWAKENING_DAILY_RESET selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static void VerifyClearWritesLootCounter(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_loot_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var dailyReset = new DailyResetService(database);
                var lootGuard = new AntonAwakeningDailyLootGuard(
                    database.ConnectionString,
                    dailyReset);
                const int accountId = 57100;
                const int characterId = 57101;
                SeedAccount(database, accountId, "anton-awakening-loot-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-loot-c");

                // 通关 243
                var marked1 = lootGuard.TryMarkLootClaimed(characterId, 243);
                Check("243 first mark succeeded", marked1, ref failures);

                // 再次通关 243：应返回 false（已标记）
                var marked2 = lootGuard.TryMarkLootClaimed(characterId, 243);
                Check("243 second mark returns false (already marked)", !marked2, ref failures);

                // 通关 244：应成功（独立副本）
                var marked3 = lootGuard.TryMarkLootClaimed(characterId, 244);
                Check("244 first mark succeeded", marked3, ref failures);

                // 检查 has-claimed
                Check("243 has claimed", lootGuard.HasClaimedLootToday(characterId, 243), ref failures);
                Check("244 has claimed", lootGuard.HasClaimedLootToday(characterId, 244), ref failures);
                Check("245 not claimed", !lootGuard.HasClaimedLootToday(characterId, 245), ref failures);

                // 非 Anton_Awakening 副本
                Check("999 not claimed", !lootGuard.HasClaimedLootToday(characterId, 999), ref failures);
                Check("999 mark returns false",
                    !lootGuard.TryMarkLootClaimed(characterId, 999), ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyCrossDayResetClearsOnlyAntonAwakening(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_cross_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                var dailyReset = new DailyResetService(database);
                const int accountId = 57110;
                const int characterId = 57111;
                SeedAccount(database, accountId, "anton-awakening-cross-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-cross-c");

                // 写入 Anton_Awakening 全部 5 行
                foreach (var dungeonId in new[] { 243, 244, 245, 246, 247 })
                {
                    repository.UpsertDungeonPermission(characterId, dungeonId, 2);
                }
                // 写入其他副本（不应被重置）
                repository.UpsertDungeonPermission(characterId, 100, 1);
                repository.UpsertDungeonPermission(characterId, 500, 1);

                var preCount = repository.LoadDungeonPermissions(characterId).Count;
                Check("pre: 7 total permissions", preCount == 7, ref failures);

                // 模拟跨天：调用 DailyResetService.TryRunAccountFirstLoginReset
                using (var conn = new SqliteConnection(database.ConnectionString))
                {
                    conn.Open();
                    using (var tx = conn.BeginTransaction(deferred: false))
                    {
                        bool applied;
                        var ok = dailyReset.TryRunAccountFirstLoginReset(
                            conn,
                            tx,
                            accountId,
                            DateTime.UtcNow,
                            resetAction: (c, t) =>
                            {
                                using (var cmd = c.CreateCommand())
                                {
                                    cmd.Transaction = t;
                                    cmd.CommandText = @"
DELETE FROM character_dungeon_permissions
WHERE dungeon_id IN (243, 244, 245, 246, 247)
  AND character_id IN (
      SELECT character_id FROM characters WHERE account_id = @aid
  );";
                                    cmd.Parameters.AddWithValue("@aid", accountId);
                                    cmd.ExecuteNonQuery();
                                }
                                return true;
                            },
                            out applied);
                        Check("cross-day reset succeeded", ok, ref failures);
                        Check("reset action applied", applied, ref failures);
                        tx.Commit();
                    }
                }

                // 验证：243-247 被清空，100 和 500 保留
                var post = repository.LoadDungeonPermissions(characterId)
                    .Select(e => e.DungeonId)
                    .ToList();
                Check("post: 100 retained", post.Contains((ushort)100), ref failures);
                Check("post: 500 retained", post.Contains((ushort)500), ref failures);
                Check("post: 243 cleared", !post.Contains((ushort)243), ref failures);
                Check("post: 247 cleared", !post.Contains((ushort)247), ref failures);
                Check("post: only 2 permissions remain", post.Count == 2, ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyClearLeavesOtherDungeonsIntact(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_other_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var repository = new SqliteCharacterStateRepository(database);
                const int accountId = 57130;
                const int characterId = 57131;
                SeedAccount(database, accountId, "anton-awakening-other-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-other-c");

                // 写入 Anton_Awakening 和 其他副本
                repository.UpsertDungeonPermission(characterId, 247, 2);
                repository.UpsertDungeonPermission(characterId, 100, 1);
                repository.UpsertDungeonPermission(characterId, 225, 1);  // Anton_Normal
                repository.UpsertDungeonPermission(characterId, 234, 1);  // Anton_Quest

                // 只清空 Anton_Awakening
                var deleted = repository.DeleteDungeonPermissions(
                    characterId,
                    new[] { 243, 244, 245, 246, 247 });
                Check("only anton_awakening deleted", deleted == 1, ref failures);

                var post = repository.LoadDungeonPermissions(characterId)
                    .Select(e => e.DungeonId)
                    .ToHashSet();
                Check("post: 100 retained", post.Contains((ushort)100), ref failures);
                Check("post: 225 (Anton_Normal) retained", post.Contains((ushort)225), ref failures);
                Check("post: 234 (Anton_Quest) retained", post.Contains((ushort)234), ref failures);
                Check("post: 247 cleared", !post.Contains((ushort)247), ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifySpecialCardDrawnOnce(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_card_once_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var dailyReset = new DailyResetService(database);
                var cardService = new AntonAwakeningDailyCardService(
                    database.ConnectionString,
                    dailyReset);
                const int accountId = 57200;
                const int characterId = 57201;
                SeedAccount(database, accountId, "anton-awakening-card-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-card-c");

                Check("not claimed before first clear",
                    !cardService.HasClaimedCardToday(characterId), ref failures);

                var firstMark = cardService.TryMarkCardClaimed(characterId);
                Check("first card mark succeeded", firstMark, ref failures);
                Check("claimed after first clear",
                    cardService.HasClaimedCardToday(characterId), ref failures);

                // 第二次通关 247：应返回 false（已标记，不会再次触发翻牌）
                var secondMark = cardService.TryMarkCardClaimed(characterId);
                Check("second card mark returns false (already claimed)",
                    !secondMark, ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyDrawCardDistribution(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_card_pool_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var dailyReset = new DailyResetService(database);
                var cardService = new AntonAwakeningDailyCardService(
                    database.ConnectionString,
                    dailyReset);
                const int accountId = 57300;
                const int characterId = 57301;
                SeedAccount(database, accountId, "anton-awakening-pool-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-pool-c");

                // 抽 30 轮，每轮 4 张，验证分布范围（池只有 6 个候选 itemId）
                var distinctItems = new HashSet<uint>();
                for (var round = 0; round < 30; round++)
                {
                    var cards = cardService.DrawCardRewards();
                    Check(
                        $"round {round}: 4 cards drawn",
                        cards.Count == 4,
                        ref failures);
                    foreach (var itemId in cards)
                    {
                        distinctItems.Add(itemId);
                        Check(
                            $"round {round}: item {itemId} in pool",
                            CardPoolItemIds.Contains(itemId),
                            ref failures);
                    }
                }

                // 1000 轮内，应能命中池里所有 itemId（卡池只有 6 项，权重最高的
                // 10157782 也只占 77%，低权重项目仍有机会命中）
                var bigDraw = new HashSet<uint>();
                for (var round = 0; round < 1000; round++)
                {
                    foreach (var itemId in cardService.DrawCardRewards())
                        bigDraw.Add(itemId);
                }
                Check("big draw covered all pool items", bigDraw.SetEquals(CardPoolItemIds), ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void VerifyCrossDayResetsCardCounter(ref int failures)
        {
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_anton_awakening_card_cross_{Guid.NewGuid():N}.db");
            try
            {
                var database = new GameDatabase(tempDbPath, ServerPaths.SchemaFilePath);
                var dailyReset = new DailyResetService(database);
                var cardService = new AntonAwakeningDailyCardService(
                    database.ConnectionString,
                    dailyReset);
                const int accountId = 57400;
                const int characterId = 57401;
                SeedAccount(database, accountId, "anton-awakening-card-cross-a");
                SeedCharacter(database, characterId, accountId, "anton-awakening-card-cross-c");

                // 当天通关 247
                var firstMark = cardService.TryMarkCardClaimed(characterId);
                Check("cross: first mark succeeded", firstMark, ref failures);
                Check("cross: claimed today",
                    cardService.HasClaimedCardToday(characterId), ref failures);

                // 模拟跨天：把 character_daily_reset 的 day_id 改为 0，
                // 下一次 EnsureRowAndRollover 会清空当日所有 counter
                using (var conn = new SqliteConnection(database.ConnectionString))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = @"
UPDATE character_daily_reset SET day_id = 0 WHERE character_id = @cid;";
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }
                }

                // 跨天后：应未领取，可再次通关
                Check("cross: after rollover, not claimed",
                    !cardService.HasClaimedCardToday(characterId), ref failures);
                var secondDayMark = cardService.TryMarkCardClaimed(characterId);
                Check("cross: second-day mark succeeded",
                    secondDayMark, ref failures);
                Check("cross: claimed second day",
                    cardService.HasClaimedCardToday(characterId), ref failures);
            }
            finally
            {
                TryDelete(tempDbPath);
                TryDelete(tempDbPath + "-wal");
                TryDelete(tempDbPath + "-shm");
            }
        }

        private static void SeedAccount(IGameDatabase database, int accountId, string mid)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, @mid, '');";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@mid", mid);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void SeedCharacter(IGameDatabase database, int characterId, int accountId, string name)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO characters (character_id, account_id, name, job)
VALUES (@cid, @aid, @name, 0);";
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@name", name);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // 忽略文件锁错误
            }
        }
    }
}