using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// Mod 设置：造价倍率、日志开关、以及社区反馈要求的访问/收纳开关。
    /// </summary>
    public class DigitalStorageSettings : ModSettings
    {
        public static float costMultiplier = 1.0f;
        public static bool enableDebugLog = false;

        /// <summary>
        /// 自动收纳：核心每 15 tick 把附近散落物品吸入账本。
        /// 关掉后仍可用搬运工单 / 缓冲仓库，只是没有「隔空收纳」。
        /// （社区反馈：「禁止自动收纳后（瞬移搬运）就没有出现了」→ 给玩家开关）
        /// </summary>
        public static bool autoIngestEnabled = true;

        /// <summary>
        /// 仅终端芯片持有者可访问核心。
        /// false（默认，v3 设计）：无芯片小人走到接口/核心代理点即可取用。
        /// true：所有核心读取（吃饭/吃药/取材/工单）都要求芯片或机械师继承。
        /// </summary>
        public static bool requireChipForCoreAccess = false;

        /// <summary>仅活动区：自动收纳只处理玩家 Home 区内的物品（防地图边缘/远矿被隔空吸走）。</summary>
        public static bool autoIngestHomeAreaOnly = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref autoIngestEnabled, "autoIngestEnabled", true);
            Scribe_Values.Look(ref requireChipForCoreAccess, "requireChipForCoreAccess", false);
            Scribe_Values.Look(ref autoIngestHomeAreaOnly, "autoIngestHomeAreaOnly", true);
            base.ExposeData();
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            Text.Font = GameFont.Medium;
            listing.Label("DS_CostSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("DS_CostMultiplier".Translate(costMultiplier));
            costMultiplier = listing.Slider(costMultiplier, 0.1f, 20f);
            listing.Gap(6f);
            listing.Label("DS_CostMultiplierDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_AccessSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_RequireChip".Translate(), ref requireChipForCoreAccess,
                "DS_RequireChipDesc".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("DS_AutoIngestToggle".Translate(), ref autoIngestEnabled,
                "DS_AutoIngestToggleDesc".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("DS_HomeAreaOnly".Translate(), ref autoIngestHomeAreaOnly,
                "DS_HomeAreaOnlyDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog,
                "DS_EnableDebugLogDesc".Translate());

            listing.End();
        }
    }
}
