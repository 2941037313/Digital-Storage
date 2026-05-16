using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Ghost
{
    /// <summary>
    /// 幽灵 Thing：不 Spawn、不渲染、不参与任何物理交互。
    /// 只存在于 listerThings 索引中，供其他 mod 扫描发现。
    /// stackCount = 真实账本量（无堆叠上限）。
    /// </summary>
    public class GhostThing : ThingWithComps
    {
        public Core.ItemKey Key;

        // ═══════════════════════════════════════════
        // 渲染：全部不画（虽然不会被调用，防御性重写）
        // ═══════════════════════════════════════════

        public override void Print(SectionLayer layer) { }
        public override void DynamicDrawPhaseAt(DrawPhase phase, Vector3 drawLoc, bool flip = false) { }
        protected override void DrawAt(Vector3 drawLoc, bool flip = false) { }
        public override void DrawGUIOverlay() { }
        public override void DrawExtraSelectionOverlays() { }

        // ═══════════════════════════════════════════
        // 消费桥接：外部取物 → 从账本扣除
        // ═══════════════════════════════════════════

        // 部分取：SplitOff → 从账本提取真货返回
        public override Thing SplitOff(int count)
        {
            if (count <= 0) return null;
            var index = Map?.GetComponent<GhostLedgerIndex>();
            if (index == null) return null;
            return index.MaterializeFromLedger(Key, count);
        }

        // 全部取：DeSpawn → 外部拿走整个 Ghost → 账本扣全额
        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            if (stackCount > 0)
            {
                var index = Map?.GetComponent<GhostLedgerIndex>();
                if (index != null)
                {
                    var withdrawn = index.MaterializeFromLedger(Key, stackCount);
                    if (withdrawn != null && !withdrawn.Destroyed)
                        withdrawn.Destroy(DestroyMode.Vanish);
                }
            }
        }

        public override bool IngestibleNow => false;
        public override bool CanStackWith(Thing other) => false;
        public override bool TryAbsorbStack(Thing other, bool respectStackLimit) => false;
        public override float MarketValue => 0f;

        public override bool PreventPlayerSellingThingsNearby(out string reason)
        {
            reason = "DS_GhostNotSellable".Translate();
            return true;
        }

        // ═══════════════════════════════════════════
        // 销毁防御：Destroy/Kill 不允许外部调用
        // ═══════════════════════════════════════════

        public override void ExposeData()
        {
            Log.Warning($"[DS-Ghost] ExposeData called on ghost: {Key}, mode={Scribe.mode}");
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish) { }
        public override void Kill(DamageInfo? dinfo = null, Hediff exactCulprit = null) { }

        // ═══════════════════════════════════════════
        // UI：不显示给玩家，但 label 保留给 mod 读取
        // ═══════════════════════════════════════════

        public override string GetInspectString() => "";
        public override IEnumerable<Gizmo> GetGizmos() { yield break; }
        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Pawn selPawn) { yield break; }
        public override IEnumerable<FloatMenuOption> GetMultiSelectFloatMenuOptions(IEnumerable<Pawn> selPawns) { yield break; }
        public override IEnumerable<InspectTabBase> GetInspectTabs() { yield break; }
        public override TipSignal GetTooltip() => default;
        public override IEnumerable<Thing> ButcherProducts(Pawn butcher, float efficiency) { yield break; }
        public override IEnumerable<Thing> SmeltProducts(float efficiency) { yield break; }
        public override void SetFaction(Faction newFaction, Pawn recruiter = null) { }
    }
}
