using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Services
{
    public class DigitalStorageGameComponent : GameComponent
    {
        private List<Building_StorageCore> globalCores = new List<Building_StorageCore>();
        private bool shown30Letter;

        public DigitalStorageGameComponent(Game game) { }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (!shown30Letter)
            {
                shown30Letter = true;
                Find.LetterStack.ReceiveLetter(
                    "DigitalStorage_Letter30_Label".Translate(),
                    "DigitalStorage_Letter30_Text".Translate(),
                    LetterDefOf.NeutralEvent);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref globalCores, "globalCores", LookMode.Reference);
            // F1: 键名必须是 shown30Letter（旧代码写 shown31Letter）。Scribe 的键一经写入
            // 就固定了，键名/字段名不一致在后续改动里极易造成「读不到 → 信每档重弹」。
            // 同时 FinalizeInit 只在 !shown30Letter 时置 true，保证下一档不再弹。
            Scribe_Values.Look(ref shown30Letter, "shown30Letter", false);
            if (Scribe.mode == LoadSaveMode.LoadingVars && globalCores == null)
            {
                globalCores = new List<Building_StorageCore>();
            }
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !globalCores.Contains(core))
            {
                globalCores.Add(core);
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                globalCores.Remove(core);
            }
        }

        public List<Building_StorageCore> GetAllCores()
        {
            // 懒清理（低频调用，不缓存）
            globalCores.RemoveAll(c => c == null || c.Destroyed);
            if (globalCores.Count > 1)
            {
                var seen = new HashSet<Building_StorageCore>();
                for (int i = globalCores.Count - 1; i >= 0; i--)
                    if (!seen.Add(globalCores[i]))
                        globalCores.RemoveAt(i);
            }
            return globalCores;
        }
    }
}
