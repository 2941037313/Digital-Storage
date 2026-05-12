using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// v3 过渡态设置 —— 留造价倍率、日志开关。
    /// 虚拟存储相关配置（预留数量、接口即时数字化等）在账本层接入前已全部移除。
    /// </summary>
    public class DigitalStorageSettings : ModSettings
    {
        public static float costMultiplier = 1.0f;
        public static bool enableDebugLog = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
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
            listing.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog,
                "DS_EnableDebugLogDesc".Translate());

            listing.End();
        }
    }
}
