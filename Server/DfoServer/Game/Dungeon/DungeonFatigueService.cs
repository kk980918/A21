using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonFatigueService
    {
        internal const int OrdinaryMaximum = 156;
        internal const int BlackDiamondMaximum = 188;

        private readonly IGameDatabase _database;
        private readonly Func<DateTime> _utcNow;

        internal DungeonFatigueService(
            IGameDatabase database,
            Func<DateTime> utcNow = null)
        {
            _database = database
                ?? throw new ArgumentNullException(nameof(database));
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal bool TryLoad(
            int characterId,
            out DungeonFatigueSnapshot snapshot)
        {
            snapshot = default;
            if (characterId <= 0)
                return false;

            try
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction(
                    deferred: false))
                {
                    if (!TryLoadAndReconcile(
                            connection,
                            transaction,
                            characterId,
                            _utcNow(),
                            out snapshot))
                    {
                        transaction.Rollback();
                        return false;
                    }

                    transaction.Commit();
                    return true;
                }
            }
            catch
            {
                snapshot = default;
                return false;
            }
        }

        internal bool TryCanEnter(int characterId, out bool allowed)
        {
            allowed = false;
            if (!TryLoad(characterId, out var snapshot))
                return false;
            allowed = snapshot.Remaining > 0;
            return true;
        }

        internal static bool RequiresAdmissionBalance(DungeonRun run)
        {
            if (IsRuntimeExempt(run))
                return false;

            try
            {
                var dungeon = GameWorld.Dungeon.GetDungeonFile(run.DungeonId);
                return dungeon == null
                    || (!dungeon.NoFatigue && !dungeon.EnterWithoutFatigue);
            }
            catch
            {
                return true;
            }
        }

        internal static bool ConsumesOnRoomMove(DungeonRun run)
        {
            if (IsRuntimeExempt(run))
                return false;

            try
            {
                var dungeon = GameWorld.Dungeon.GetDungeonFile(run.DungeonId);
                return dungeon == null
                    || (!dungeon.NoFatigue
                        && !dungeon.EnterWithoutFatigue
                        && !dungeon.UseFatigueOnlyStartDungeon);
            }
            catch
            {
                return true;
            }
        }

        internal bool TryConsumeRooms(
            IReadOnlyCollection<int> characterIds,
            out IReadOnlyDictionary<int, DungeonFatigueSnapshot> snapshots)
        {
            snapshots = new Dictionary<int, DungeonFatigueSnapshot>();
            var ids = characterIds?
                .Where(characterId => characterId > 0)
                .Distinct()
                .ToArray();
            if (ids == null || ids.Length == 0)
                return false;

            try
            {
                using (var connection = _database.OpenConnection())
                using (var transaction = connection.BeginTransaction(
                    deferred: false))
                {
                    var now = _utcNow();
                    var updated = new Dictionary<int, DungeonFatigueSnapshot>(
                        ids.Length);
                    foreach (var characterId in ids)
                    {
                        if (!TryLoadAndReconcile(
                                connection,
                                transaction,
                                characterId,
                                now,
                                out var current))
                        {
                            transaction.Rollback();
                            return false;
                        }

                        var next = new DungeonFatigueSnapshot(
                            current.CharacterId,
                            Math.Max(0, current.Remaining - 1),
                            current.Used,
                            current.Maximum,
                            current.ResetDay);
                        using (var command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = @"
UPDATE characters
SET fatigue=@remaining,
    updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid;";
                            command.Parameters.AddWithValue(
                                "@remaining",
                                next.Remaining);
                            command.Parameters.AddWithValue(
                                "@cid",
                                characterId);
                            if (command.ExecuteNonQuery() != 1)
                            {
                                transaction.Rollback();
                                return false;
                            }
                        }
                        updated.Add(characterId, next);
                    }

                    transaction.Commit();
                    snapshots = updated;
                    return true;
                }
            }
            catch
            {
                snapshots = new Dictionary<int, DungeonFatigueSnapshot>();
                return false;
            }
        }

        internal void RegisterClock(
            ClockService clock,
            Game.Session.ISessionDirectory sessions)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));
            if (sessions == null)
                throw new ArgumentNullException(nameof(sessions));

            clock.RegisterDailyMoment(
                "dungeon-fatigue-reset",
                hour: 6,
                minute: 0,
                utcNow => _ = RefreshOnlineAsync(sessions));
        }

        private async Task RefreshOnlineAsync(
            Game.Session.ISessionDirectory sessions)
        {
            foreach (var session in sessions.GetAllGameSessions())
            {
                var characterId = session?.Player?.CharacterId ?? 0;
                if (characterId <= 0
                    || !TryLoad(characterId, out var snapshot))
                {
                    continue;
                }

                var packet = GamePacketEnvelopeBuilder.Build(
                    0,
                    (ushort)NotiPacketTypeA21.FATIGUE,
                    DungeonNotificationBuilder.BuildFatigue(
                        snapshot.Remaining,
                        snapshot.Used,
                        snapshot.Maximum,
                        fatigueBattery: 0,
                        fatigueGrownUpBuff: 0));
                try
                {
                    await session.TrySendPacketAsync(
                        packet,
                        CancellationToken.None,
                        () => sessions.TryGet(characterId, out var current)
                            && ReferenceEquals(current, session)
                            && session.Player?.CharacterId == characterId);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonFatigue] daily reset notification failed " +
                        $"cid={characterId}: {ex.Message}");
                }
            }
        }

        private static bool TryLoadAndReconcile(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DateTime utcNow,
            out DungeonFatigueSnapshot snapshot)
        {
            snapshot = default;
            int accountId;
            int remaining;
            int used;
            int oldMaximum;
            int resetDay;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT account_id, fatigue, usedFatigue, maxFatigue, fatigue_reset_day
FROM characters
WHERE character_id=@cid AND delete_flag=0;";
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;
                    accountId = reader.GetInt32(0);
                    remaining = reader.GetInt32(1);
                    used = reader.GetInt32(2);
                    oldMaximum = reader.GetInt32(3);
                    resetDay = reader.GetInt32(4);
                }
            }

            var now = NormalizeUtc(utcNow);
            var maximum = PremiumService.HasActiveBlackDiamond(
                    connection,
                    transaction,
                    accountId,
                    new DateTimeOffset(now).ToUnixTimeSeconds())
                ? BlackDiamondMaximum
                : OrdinaryMaximum;
            var today = DailyResetService.TodayId(now);
            if (resetDay != today)
            {
                remaining = maximum;
                used = 0;
                resetDay = today;
            }
            else
            {
                oldMaximum = oldMaximum > 0
                    ? oldMaximum
                    : maximum;
                var consumed = Math.Max(
                    0,
                    oldMaximum - Math.Max(0, Math.Min(oldMaximum, remaining)));
                remaining = Math.Max(0, maximum - consumed);
                used = Math.Max(0, Math.Min(ushort.MaxValue, used));
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE characters
SET fatigue=@remaining,
    usedFatigue=@used,
    maxFatigue=@maximum,
    fatigue_reset_day=@day,
    updated_at=CURRENT_TIMESTAMP
WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@remaining", remaining);
                command.Parameters.AddWithValue("@used", used);
                command.Parameters.AddWithValue("@maximum", maximum);
                command.Parameters.AddWithValue("@day", resetDay);
                command.Parameters.AddWithValue("@cid", characterId);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            snapshot = new DungeonFatigueSnapshot(
                characterId,
                remaining,
                used,
                maximum,
                resetDay);
            return true;
        }

        private static DateTime NormalizeUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc
                ? value
                : value.Kind == DateTimeKind.Local
                    ? value.ToUniversalTime()
                    : DateTime.SpecifyKind(value, DateTimeKind.Utc);

        private static bool IsRuntimeExempt(DungeonRun run)
            => run == null
                || run.Tower != null
                || run.PracticeMode
                || run.RewardPolicy.Kind
                    == DungeonRewardPolicyKind.InteractiveTraining
                || GameWorld.Dungeon.TryGetTowerOfDespairFloor(
                    run.DungeonId,
                    out _);
    }

    internal readonly struct DungeonFatigueSnapshot
    {
        internal DungeonFatigueSnapshot(
            int characterId,
            int remaining,
            int used,
            int maximum,
            int resetDay)
        {
            CharacterId = characterId;
            Remaining = remaining;
            Used = used;
            Maximum = maximum;
            ResetDay = resetDay;
        }

        internal int CharacterId { get; }
        internal int Remaining { get; }
        internal int Used { get; }
        internal int Maximum { get; }
        internal int ResetDay { get; }
    }
}
