using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 缓冲仓库 UI：Gizmo 按钮 + 核心绑定 FloatMenu。
    /// </summary>
    public partial class Building_BufferWarehouse
    {
        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
                yield return g;

            if (boundCore != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DS_BufferBoundTo".Translate(boundCore.LabelCap),
                    defaultDesc = "DS_BufferBoundToDesc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = delegate
                    {
                        Find.WindowStack.Add(new FloatMenu(GetBindOptions().ToList()));
                    }
                };
            }
            else
            {
                yield return new Command_Action
                {
                    defaultLabel = "DS_BufferBindCore".Translate(),
                    defaultDesc = "DS_BufferBindCoreDesc".Translate(),
                    icon = TexCommand.DesirePower,
                    action = delegate
                    {
                        Find.WindowStack.Add(new FloatMenu(GetBindOptions().ToList()));
                    }
                };
            }
        }

        private IEnumerable<FloatMenuOption> GetBindOptions()
        {
            var mapComp = Map?.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null) yield break;

            if (boundCore != null)
            {
                yield return new FloatMenuOption("DS_BufferUnbind".Translate(), delegate
                {
                    UnbindFromCore();
                });
            }

            foreach (var core in mapComp.GetAllCores())
            {
                if (core == null || core.Destroyed || !core.Spawned) continue;
                var c = core;
                string label = core == boundCore
                    ? "DS_BufferCurrentCore".Translate(c.LabelCap, c.NetworkName)
                    : "DS_BufferSelectCore".Translate(c.LabelCap, c.NetworkName);
                yield return new FloatMenuOption(label, delegate
                {
                    BindToCore(c);
                });
            }
        }
    }
}
