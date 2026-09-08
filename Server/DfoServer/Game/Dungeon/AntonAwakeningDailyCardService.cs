using DfoServer.Game.DailyReset;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Game.Dungeon
{
    /// <summary>
    /// 暴走安徒恩 247 特殊翻牌服务：
    /// - 每天首次通关 247 触发 4 张卡翻牌
    /// - 卡池来源：etc/raid/anton.etc [PHASE NAME] "灭杀安徒恩" 段
    ///   squad_item (10157782 史诗/10157783 灵魂碎片/10157784 源助力/
    ///   10157785 史诗/10157786 心脏卡片) + pcroom_card (10094733 浓缩魔能石)
    /// - 跨天 06:00 通过 character_daily_counters 自动归零
    /// </summary>
    internal sealed class AntonAwakeningDailyCardService
    {
        public const int FinalDungeonId = 247;
        private const string CardCounterKey = "anton_awakening_card_247";
        private const int CardCount = 4;

        // etc/raid/anton.etc 中 [PHASE NAME] "灭杀安徒恩" 段的索引（0-indexed）。
        // SplitPhaseBlocks 跳过文件头（[PHASE MAX] 等之前的配置），仅保留
        // 真正的 [PHASE] 段：result[0] = 阻截安徒恩，result[1] = 灭杀安徒恩。
        private const int AwakeningPhaseIndex = 1;

        // 抽卡池：squad_item + pcroom_card 两种 rewardType
        private static readonly string[] CardRewardTypes = { "squad_item", "pcroom_card" };

        private readonly string _connectionString;
        private readonly DailyResetService _dailyReset;
        private readonly List<RaidStateReward> _candidates;
        private readonly int _totalWeight;
        private readonly bool _configured;

        internal AntonAwakeningDailyCardService(
            string connectionString,
            DailyResetService dailyReset = null)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
            _dailyReset = dailyReset;

            // 加载 etc/raid/anton.etc 抽卡池（一次性初始化）
            try
            {
                var config = PvfArchiveAccessor.ReadText("etc/raid/anton.etc");
                var phase = ParsePhase(config, AwakeningPhaseIndex);
                _candidates = phase ?? new List<RaidStateReward>();
                _totalWeight = _candidates.Sum(c => Math.Max(0, c.Weight));
                _configured = _candidates.Count > 0 && _totalWeight > 0;
                FileLogger.Log(
                    $"[AntonAwakeningDailyCardService] loaded phase={AwakeningPhaseIndex} "
                    + $"candidates={_candidates.Count} totalWeight={_totalWeight} configured={_configured}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[AntonAwakeningDailyCardService] failed to load etc/raid/anton.etc: {ex.Message}");
                _candidates = new List<RaidStateReward>();
                _totalWeight = 0;
                _configured = false;
            }
        }

        /// <summary>
        /// 标记今天已触发特殊翻牌（cap=1）。
        /// </summary>
        internal bool TryMarkCardClaimed(int characterId)
        {
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
                        CardCounterKey,
                        cap: 1,
                        period: DailyResetService.PeriodDay);
                    tx.Commit();
                    return applied;
                }
            }
        }

        /// <summary>
        /// 检查今天是否已经触发过特殊翻牌。
        /// </summary>
        internal bool HasClaimedCardToday(int characterId)
        {
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
                        CardCounterKey);
                    tx.Commit();
                    return count > 0;
                }
            }
        }

        /// <summary>
        /// 抽取 CardCount 张卡（每张 1 个 itemId）。
        /// 池来源：etc/raid/anton.etc 灭杀安徒恩段的 squad_item + pcroom_card。
        /// </summary>
        internal IReadOnlyList<uint> DrawCardRewards()
        {
            var result = new List<uint>(CardCount);
            if (!_configured)
                return result;

            for (var i = 0; i < CardCount; i++)
            {
                var roll = Random.Shared.Next(_totalWeight);
                var cumulative = 0;
                foreach (var candidate in _candidates)
                {
                    cumulative += Math.Max(0, candidate.Weight);
                    if (roll < cumulative)
                    {
                        if (candidate.ItemId > 0)
                            result.Add(checked((uint)candidate.ItemId));
                        break;
                    }
                }
            }
            return result;
        }

        // 解析 etc/raid/anton.etc 中指定 phase 的 [state reward] 列表
        // 复用 AntonRaidRewardProvider 相同的脚本格式（rewardType state weight itemId flags）
        private static List<RaidStateReward> ParsePhase(string config, int phaseIndex)
        {
            if (string.IsNullOrWhiteSpace(config))
                return null;

            var phaseNodes = SplitPhaseBlocks(config);
            if (phaseIndex < 0 || phaseIndex >= phaseNodes.Count)
                return null;

            var phaseContent = phaseNodes[phaseIndex];
            var result = new List<RaidStateReward>();
            foreach (var line in ExtractStateRewardLines(phaseContent))
            {
                var parts = line.Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                // 实际数据中 rewardType 被反引号包裹（`squad_item`），先去除
                var rewardType = StripBacktick(parts[0]);
                if (Array.IndexOf(CardRewardTypes, rewardType) < 0) continue;
                if (!int.TryParse(parts[1], out var state)) continue;
                if (!int.TryParse(parts[2], out var weight)) continue;
                if (!int.TryParse(parts[3], out var itemId)) continue;
                if (!int.TryParse(parts[4], out var flags)) continue;
                if (weight <= 0) continue;
                result.Add(new RaidStateReward
                {
                    RewardType = rewardType,
                    State = state,
                    Weight = weight,
                    ItemId = itemId,
                    Flags = flags,
                });
            }
            return result;
        }

        // 按 [PHASE] 拆开；只保留含有 [/PHASE] 的真正 phase 段（跳过文件头配置）。
        private static List<string> SplitPhaseBlocks(string config)
        {
            var result = new List<string>();
            var current = "";
            foreach (var rawLine in config.Split('\n'))
            {
                var trimmed = rawLine.Trim();
                if (trimmed.Equals("[phase]", StringComparison.OrdinalIgnoreCase))
                {
                    if (current.Length > 0
                        && current.IndexOf("[/phase]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Add(current);
                    }
                    current = "";
                }
                else
                {
                    current += rawLine + "\n";
                }
            }
            if (current.Length > 0
                && current.IndexOf("[/phase]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Add(current);
            }
            return result;
        }

        // 提取 [state reward] 行
        private static IEnumerable<string> ExtractStateRewardLines(string phaseContent)
        {
            var inStateReward = false;
            foreach (var line in phaseContent.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Equals("[state reward]", StringComparison.OrdinalIgnoreCase))
                {
                    inStateReward = true;
                    continue;
                }
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inStateReward = false;
                    continue;
                }
                if (inStateReward && trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }

        // 去除 PVF 中反引号包裹的 rewardType（`squad_item` → squad_item）
        private static string StripBacktick(string token)
        {
            if (string.IsNullOrEmpty(token) || token.Length < 2)
                return token;
            if (token[0] == '`' && token[token.Length - 1] == '`')
                return token.Substring(1, token.Length - 2);
            return token;
        }

        // 内部数据结构（与 PvfLib.RaidStateReward 同名私有）
        private sealed class RaidStateReward
        {
            public string RewardType { get; set; }
            public int State { get; set; }
            public int Weight { get; set; }
            public int ItemId { get; set; }
            public int Flags { get; set; }
        }
    }
}
