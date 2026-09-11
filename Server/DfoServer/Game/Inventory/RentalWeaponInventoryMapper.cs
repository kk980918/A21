using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using DfoServer.GameWorld;
using PvfLib;

namespace DfoServer.Game.Inventory
{
    /// 校验 A21 租赁目录中的装备模板，并读取 PVF 定义的幸运星价格与强化等级。
    public static class RentalWeaponInventoryMapper
    {
        private const string RentalCatalogPath = "etc/chnrentsystem/rentsysteminfo.etc";

        // [section] 每档一行: 档位 天数? 强化等级 ... (共 7 列, 第 3 列为强化等级)
        private const int RentalSectionColumnCount = 7;
        private const int RentalSectionUpgradeColumn = 2;

        private sealed class RentalWeaponIdentity
        {
            public int StarPrice { get; set; }

            public int Tier { get; set; } = -1;

            public byte UpgradeLevel { get; set; }
        }

        private static readonly Lazy<Dictionary<int, RentalWeaponIdentity>> IdentityById =
            new Lazy<Dictionary<int, RentalWeaponIdentity>>(BuildIdentityIndex);

        public static bool IsValidInventoryTemplate(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return false;

            return IdentityById.Value.ContainsKey(itemTemplateId);
        }

        public static int GetStarPrice(int inventoryTemplateId)
        {
            if (IdentityById.Value.TryGetValue(inventoryTemplateId, out var identity) && identity.StarPrice > 0)
                return identity.StarPrice;

            return 0;
        }

        /// 租赁货架上的装备自带强化等级([section] 第 3 列), 授予时必须写入 ItemCore.Upgrade。
        public static byte GetRentalUpgradeLevel(int itemTemplateId)
        {
            return IdentityById.Value.TryGetValue(itemTemplateId, out var identity)
                ? identity.UpgradeLevel
                : (byte)0;
        }

        private static Dictionary<int, RentalWeaponIdentity> BuildIdentityIndex()
        {
            var byId = new Dictionary<int, RentalWeaponIdentity>();
            var catalogText = PvfArchiveAccessor.ReadText(RentalCatalogPath);
            var catalog = ParseRentalCatalog(catalogText);
            if (catalog.Count == 0)
                throw new InvalidOperationException($"PVF {RentalCatalogPath} contains no rental package selections.");

            var tierByItem = ParseRentalItemTiers(catalogText);
            var upgradeByTier = ParseRentalSectionUpgrades(catalogText);

            var lst = LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst"));
            var equipmentIds = new HashSet<int>();
            foreach (var entry in lst.Entries)
            {
                equipmentIds.Add(entry.Id);
            }

            foreach (var item in catalog)
            {
                if (!equipmentIds.Contains(item.Key))
                {
                    throw new InvalidOperationException(
                        $"PVF {RentalCatalogPath} references missing equipment item {item.Key}.");
                }

                var tier = tierByItem.TryGetValue(item.Key, out var resolvedTier) ? resolvedTier : -1;
                var upgradeLevel = (byte)0;
                if (tier >= 0 && upgradeByTier.TryGetValue(tier, out var resolvedUpgrade))
                {
                    upgradeLevel = resolvedUpgrade;
                }
                else
                {
                    FileLogger.Log(
                        $"[RentalWeaponInventoryMapper] rental item {item.Key} has no [section] entry "
                        + $"(tier={tier}); granted item keeps upgrade 0.");
                }

                byId[item.Key] = new RentalWeaponIdentity
                {
                    StarPrice = item.Value,
                    Tier = tier,
                    UpgradeLevel = upgradeLevel,
                };
            }

            return byId;
        }

        /// 解析 [group] 块, 得到 物品 -> 档位(组索引) 的映射; 同一物品只属于一个档位。
        internal static IReadOnlyDictionary<int, int> ParseRentalItemTiers(string text)
        {
            var tiers = new Dictionary<int, int>();
            var currentTier = -1;
            var inPackageSelection = false;
            foreach (var rawLine in SplitLines(text))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[group]", StringComparison.OrdinalIgnoreCase))
                {
                    currentTier = -1;
                    inPackageSelection = false;
                    continue;
                }

                if (line.Equals("[/group]", StringComparison.OrdinalIgnoreCase))
                {
                    currentTier = -1;
                    inPackageSelection = false;
                    continue;
                }

                if (line.Equals("[package selection]", StringComparison.OrdinalIgnoreCase))
                {
                    inPackageSelection = true;
                    continue;
                }

                if (line.Equals("[/package selection]", StringComparison.OrdinalIgnoreCase))
                {
                    inPackageSelection = false;
                    continue;
                }

                if (!inPackageSelection)
                {
                    // 组头形如: `[swordman]` 3
                    var header = Regex.Match(line, @"^`[^`]*`\s+(?<tier>\d+)$");
                    if (header.Success
                        && int.TryParse(header.Groups["tier"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tier))
                    {
                        currentTier = tier;
                    }

                    continue;
                }

                var matches = Regex.Matches(line, @"-?\d+");
                for (var index = 0; index + 1 < matches.Count; index += 2)
                {
                    if (int.TryParse(matches[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                        && itemId > 0)
                    {
                        tiers[itemId] = currentTier;
                    }
                }
            }

            return tiers;
        }

        /// 解析 [section] 块, 得到 档位 -> 强化等级 的映射(每档 7 列, 第 3 列为强化等级)。
        internal static IReadOnlyDictionary<int, byte> ParseRentalSectionUpgrades(string text)
        {
            var upgrades = new Dictionary<int, byte>();
            var values = new List<int>();
            var inSection = false;
            foreach (var rawLine in SplitLines(text))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[section]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    values.Clear();
                    continue;
                }

                if (line.Equals("[/section]", StringComparison.OrdinalIgnoreCase))
                {
                    inSection = false;
                    continue;
                }

                if (!inSection)
                    continue;

                foreach (Match match in Regex.Matches(line, @"-?\d+"))
                {
                    if (int.TryParse(match.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                        values.Add(parsed);
                }
            }

            for (var index = 0; index + RentalSectionColumnCount <= values.Count; index += RentalSectionColumnCount)
            {
                var tier = values[index];
                var upgrade = values[index + RentalSectionUpgradeColumn];
                if (tier < 0 || upgrade <= 0 || upgrade > 31)
                    continue;

                upgrades[tier] = (byte)upgrade;
            }

            return upgrades;
        }

        internal static IReadOnlyDictionary<int, int> ParseRentalCatalog(string text)
        {
            var catalog = new Dictionary<int, int>();
            var inPackageSelection = false;
            foreach (var rawLine in SplitLines(text))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                if (line.Equals("[package selection]", StringComparison.OrdinalIgnoreCase))
                {
                    inPackageSelection = true;
                    continue;
                }

                if (line.StartsWith("[/", StringComparison.Ordinal))
                {
                    inPackageSelection = false;
                    continue;
                }

                if (!inPackageSelection)
                    continue;

                var matches = Regex.Matches(line, @"-?\d+");
                if (matches.Count == 0)
                    continue;
                if ((matches.Count & 1) != 0)
                    throw new FormatException($"PVF {RentalCatalogPath} contains an incomplete rental item/price pair: {line}");

                for (var index = 0; index < matches.Count; index += 2)
                {
                    if (!int.TryParse(matches[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
                        || !int.TryParse(matches[index + 1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var starPrice)
                        || itemId <= 0
                        || starPrice <= 0)
                    {
                        throw new FormatException($"PVF {RentalCatalogPath} contains an invalid rental item/price pair: {line}");
                    }

                    if (catalog.TryGetValue(itemId, out var previousPrice) && previousPrice != starPrice)
                    {
                        throw new FormatException(
                            $"PVF {RentalCatalogPath} assigns conflicting prices to item {itemId}: {previousPrice} and {starPrice}.");
                    }

                    catalog[itemId] = starPrice;
                }
            }

            return catalog;
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? string.Empty).Split(
                new[] { '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
