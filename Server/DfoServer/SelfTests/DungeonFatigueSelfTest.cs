using System;
using System.IO;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class DungeonFatigueSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DUNGEON_FATIGUE selftest ===");
            var failures = 0;

            VerifyNewSchema(ref failures);
            VerifyV26MissingColumnsMigration(ref failures);
            VerifyLegacyConsumedValueMigration(ref failures);
            VerifyCharacterRepositoryMapping(ref failures);
            VerifyPersistenceBlackDiamondAndRollover(ref failures);
            VerifyFatigueNotificationBody(ref failures);
            VerifySelectCharacterFatigueProjection(ref failures);
            VerifyDungeonFatiguePolicy(ref failures);
            VerifyAdmissionRejectMapping(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "DUNGEON_FATIGUE selftest passed."
                    : $"DUNGEON_FATIGUE selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyFatigueNotificationBody(ref int failures)
        {
            var body = DungeonNotificationBuilder.BuildFatigue(
                remaining: 155,
                used: 0,
                maximum: 156,
                fatigueBattery: 0,
                fatigueGrownUpBuff: 0);
            Check(
                "A21 fatigue notification projects remaining value as consumed value",
                body.Length == 10
                && BitConverter.ToString(body)
                    == "01-00-9C-00-00-00-00-00-00-00",
                ref failures);
        }

        private static void VerifySelectCharacterFatigueProjection(
            ref int failures)
        {
            var snapshot = new Game.SelectCharacter.SelectCharacterDataSnapshot
            {
                CharacterRecord = new Game.Characters.CharacterRecord
                {
                    CreatedAt = DateTime.UnixEpoch,
                    Fatigue = 155,
                    MaxFatigue = 156,
                    UsedFatigue = 0
                }
            };

            Check(
                "select character ACK projects remaining fatigue",
                SelectCharacterAckBodyBuilder.TryBuild(snapshot, out var body)
                && BitConverter.ToInt16(body, 11) == 1
                && BitConverter.ToInt16(body, 13) == 156
                && BitConverter.ToInt16(body, 15) == 0,
                ref failures);
        }

        private static void VerifyDungeonFatiguePolicy(ref int failures)
        {
            var practiceRequest = SelectDungeonRequest.Parse(new byte[]
            {
                0x9C, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x01,
                0xFF, 0xFF,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            });
            var practice = new DungeonRun(
                (short)practiceRequest.DungeonId,
                practiceRequest.Difficulty)
            {
                PracticeMode = practiceRequest.PracticeMode,
            };
            var partyPractice = new DungeonRun();
            new DungeonSelectionSnapshot
            {
                PracticeMode = practice.PracticeMode,
            }.ApplyTo(partyPractice);
            Check(
                "captured A21 practice selection skips fatigue",
                practiceRequest.PracticeMode
                && !new SelectDungeonRequest(156, 1, 1, 1).PracticeMode
                && !DungeonFatigueService.RequiresAdmissionBalance(practice)
                && !DungeonFatigueService.ConsumesOnRoomMove(practice)
                && !DungeonFatigueService.ConsumesOnRoomMove(partyPractice),
                ref failures);

            var ordinary = new DungeonRun(1, 0);
            Check(
                "ordinary dungeon checks and consumes fatigue",
                DungeonFatigueService.RequiresAdmissionBalance(ordinary)
                && DungeonFatigueService.ConsumesOnRoomMove(ordinary),
                ref failures);

            var training = new DungeonRun(
                new DungeonInstance(
                    1,
                    0,
                    DungeonRewardPolicy.InteractiveTraining),
                runId: 1,
                runGeneration: 1,
                DungeonRunState.Active);
            Check(
                "interactive training skips fatigue",
                !DungeonFatigueService.RequiresAdmissionBalance(training)
                && !DungeonFatigueService.ConsumesOnRoomMove(training),
                ref failures);

            Check(
                "tower of despair skips fatigue",
                GameWorld.Dungeon.TryGetTowerOfDespairDungeonId(
                    1,
                    out var towerDungeonId)
                && !DungeonFatigueService.RequiresAdmissionBalance(
                    new DungeonRun((short)towerDungeonId, 0))
                && !DungeonFatigueService.ConsumesOnRoomMove(
                    new DungeonRun((short)towerDungeonId, 0)),
                ref failures);
        }

        private static void VerifyAdmissionRejectMapping(ref int failures)
        {
            var rejection = Network.Handlers.Dungeon.DungeonEntryHandler
                .ResolveEntryAdmissionReject(
                    new EntryCostResult().Fail(
                        "fatigue exhausted",
                        EntryCostFailureKind.InsufficientFatigue),
                    memberSlot: 3);
            Check(
                "exhausted member maps to typed fatigue rejection",
                rejection.Reason
                    == DungeonAdmissionRejectReason.InsufficientFatigue
                && rejection.MemberSlot == 3,
                ref failures);
        }

        private static void VerifyPersistenceBlackDiamondAndRollover(
            ref int failures)
        {
            var databasePath = TempDbPath("service");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                var beforeResetUtc = new DateTime(
                    2026,
                    9,
                    12,
                    21,
                    59,
                    59,
                    DateTimeKind.Utc);
                var afterResetUtc = new DateTime(
                    2026,
                    9,
                    12,
                    22,
                    0,
                    0,
                    DateTimeKind.Utc);
                var expire = new DateTimeOffset(afterResetUtc.AddDays(1))
                    .ToUnixTimeSeconds();
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES
    (9651, 'fatigue-normal-account', ''),
    (9661, 'fatigue-black-diamond-account', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES
    (9652, 9651, 'fatigue-normal-character', 0),
    (9662, 9661, 'fatigue-black-diamond-character', 0);
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES(9661, 17, @expire);";
                    command.Parameters.AddWithValue("@expire", expire);
                    command.ExecuteNonQuery();
                }

                var service = new DungeonFatigueService(
                    database,
                    () => beforeResetUtc);
                Check(
                    "first load resets ordinary and black diamond maximums",
                    service.TryLoad(9652, out var normal)
                    && normal.Remaining == 156
                    && normal.Maximum == 156
                    && service.TryLoad(9662, out var blackDiamond)
                    && blackDiamond.Remaining == 188
                    && blackDiamond.Maximum == 188,
                    ref failures);

                Check(
                    "three party room switches persist fatigue for every member",
                    service.TryConsumeRooms(
                        new[] { 9652, 9662 },
                        out _)
                    && service.TryConsumeRooms(
                        new[] { 9652, 9662 },
                        out _)
                    && service.TryConsumeRooms(
                        new[] { 9652, 9662 },
                        out var consumed)
                    && consumed[9652].Remaining == 153
                    && consumed[9662].Remaining == 185
                    && ReadFatigue(database, 9652).Remaining == 153
                    && ReadFatigue(database, 9662).Remaining == 185,
                    ref failures);

                var reloaded = new DungeonFatigueService(
                    database,
                    () => beforeResetUtc);
                Check(
                    "same game day reload preserves consumed fatigue",
                    reloaded.TryLoad(9652, out var sameDay)
                    && sameDay.Remaining == 153,
                    ref failures);

                SetRemaining(database, 9652, 0);
                SetRemaining(database, 9662, 0);
                Check(
                    "ordinary and black diamond admission reject exhausted fatigue",
                    reloaded.TryCanEnter(9652, out var exhausted)
                    && !exhausted,
                    ref failures);
                Check(
                    "black diamond exhausted state retains 188 maximum",
                    reloaded.TryCanEnter(9662, out var blackDiamondExhausted)
                    && !blackDiamondExhausted
                    && reloaded.TryLoad(9662, out var exhaustedBlackDiamond)
                    && exhaustedBlackDiamond.Maximum == 188,
                    ref failures);

                var nextDay = new DungeonFatigueService(
                    database,
                    () => afterResetUtc);
                Check(
                    "Beijing 06:00 rollover restores ordinary fatigue",
                    nextDay.TryLoad(9652, out var reset)
                    && reset.Remaining == 156
                    && reset.ResetDay
                        == DailyResetService.TodayId(afterResetUtc)
                    && nextDay.TryLoad(9652, out var repeatedReset)
                    && repeatedReset.Remaining == 156,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyCharacterRepositoryMapping(ref int failures)
        {
            var databasePath = TempDbPath("repository");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(9641, 'fatigue-repository-account', '');
INSERT INTO characters(
    character_id, account_id, name, job,
    fatigue, usedFatigue, maxFatigue, fatigue_reset_day
) VALUES (
    9642, 9641, 'fatigue-repository-character', 0,
    155, 2, 188, 20260912
);";
                    command.ExecuteNonQuery();
                }

                var record = new Game.Characters.SqliteCharacterRepository(
                    database).GetById(9642);
                Check(
                    "character repository maps remaining fatigue state",
                    record != null
                    && record.Fatigue == 155
                    && record.UsedFatigue == 2
                    && record.MaxFatigue == 188
                    && record.FatigueResetDay == 20260912,
                    ref failures);
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyV26MissingColumnsMigration(ref int failures)
        {
            var databasePath = TempDbPath("v26-migration");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(9621, 'fatigue-v26-account', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES(9622, 9621, 'fatigue-v26-character', 0);
ALTER TABLE characters DROP COLUMN fatigue;
ALTER TABLE characters DROP COLUMN usedFatigue;
ALTER TABLE characters DROP COLUMN maxFatigue;
ALTER TABLE characters DROP COLUMN fatigue_reset_day;
UPDATE schema_metadata SET schema_version=26 WHERE singleton_id=1;
PRAGMA user_version=26;";
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                    var migrated =
                        SqliteMigrations.ReadVersion(connection) == 27
                        && ColumnExists(connection, "fatigue")
                        && ColumnExists(connection, "usedFatigue")
                        && ColumnExists(connection, "maxFatigue")
                        && ColumnExists(connection, "fatigue_reset_day");
                    Check(
                        "schema v26 missing fatigue columns migrates to remaining fatigue",
                        migrated
                        && ReadFatigue(connection, 9622)
                            == (156, 0, 156, DailyResetService.TodayId()),
                        ref failures);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyLegacyConsumedValueMigration(ref int failures)
        {
            var databasePath = TempDbPath("legacy-migration");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    var expire = DateTimeOffset.UtcNow.AddDays(1)
                        .ToUnixTimeSeconds();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(9631, 'fatigue-legacy-account', '');
INSERT INTO characters(
    character_id, account_id, name, job,
    fatigue, usedFatigue, maxFatigue, fatigue_reset_day
) VALUES (
    9632, 9631, 'fatigue-legacy-character', 0,
    10, 0, 188, 0
);
INSERT INTO account_premiums(account_id, premium_type, end_time)
VALUES(9631, 17, @expire);
UPDATE schema_metadata SET schema_version=21 WHERE singleton_id=1;
PRAGMA user_version=21;";
                        command.Parameters.AddWithValue("@expire", expire);
                        command.ExecuteNonQuery();
                    }

                    SqliteMigrations.Apply(connection);
                    Check(
                        "schema v21 converts consumed fatigue once for black diamond",
                        SqliteMigrations.ReadVersion(connection) == 27
                        && ReadFatigue(connection, 9632)
                            == (178, 0, 188, DailyResetService.TodayId()),
                        ref failures);
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static void VerifyNewSchema(ref int failures)
        {
            var databasePath = TempDbPath("schema");
            try
            {
                var database = new GameDatabase(
                    databasePath,
                    ServerPaths.SchemaFilePath);
                using (var connection = database.OpenConnection())
                {
                    var hasColumns =
                        ColumnExists(connection, "fatigue")
                        && ColumnExists(connection, "usedFatigue")
                        && ColumnExists(connection, "maxFatigue")
                        && ColumnExists(connection, "fatigue_reset_day");
                    Check(
                        "new schema creates remaining fatigue columns",
                        hasColumns,
                        ref failures);

                    if (!hasColumns)
                        return;

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
INSERT INTO accounts(account_id, m_id, password_hash)
VALUES(9601, 'fatigue-schema-account', '');
INSERT INTO characters(character_id, account_id, name, job)
VALUES(9611, 9601, 'fatigue-schema-character', 0);
SELECT fatigue, usedFatigue, maxFatigue, fatigue_reset_day
FROM characters
WHERE character_id=9611;";
                        using (var reader = command.ExecuteReader())
                        {
                            Check(
                                "new ordinary character starts with 156 remaining fatigue",
                                reader.Read()
                                && reader.GetInt32(0) == 156
                                && reader.GetInt32(1) == 0
                                && reader.GetInt32(2) == 156
                                && reader.GetInt32(3) == 0,
                                ref failures);
                        }
                    }
                }
            }
            finally
            {
                TryDelete(databasePath);
            }
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(characters);";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static (int Remaining, int Used, int Maximum, int DayId)
            ReadFatigue(SqliteConnection connection, int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT fatigue, usedFatigue, maxFatigue, fatigue_reset_day
FROM characters
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return default;
                    return (
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3));
                }
            }
        }

        private static (int Remaining, int Used, int Maximum, int DayId)
            ReadFatigue(GameDatabase database, int characterId)
        {
            using (var connection = database.OpenConnection())
                return ReadFatigue(connection, characterId);
        }

        private static void SetRemaining(
            GameDatabase database,
            int characterId,
            int remaining)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
UPDATE characters
SET fatigue=@remaining
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@remaining", remaining);
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static string TempDbPath(string purpose)
            => Path.Combine(
                Path.GetTempPath(),
                $"dfo_dungeon_fatigue_{purpose}_{Guid.NewGuid():N}.db");

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (File.Exists(path + "-wal"))
                    File.Delete(path + "-wal");
                if (File.Exists(path + "-shm"))
                    File.Delete(path + "-shm");
            }
            catch
            {
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
