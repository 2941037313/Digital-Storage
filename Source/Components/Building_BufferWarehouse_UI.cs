using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 缓冲仓库 UI：Gizmo 按钮 + 核心绑定 FloatMenu。
    /// 8.1: 屏蔽原版存储设置按钮（复制/粘贴设置、存储组）——缓冲仓库的设置
    /// 唯一入口是新 UI（ITab:放什么物品 + Min/Max + 清空该单元），防止玩家
    /// 误改存储筛选/优先级导致物品从仓库被搬运前往其他存储区。
    /// </summary>
    public partial class Building_BufferWarehouse
    {
        private static readonly Texture2D CopySettingsTex =
            ContentFinder<Texture2D>.Get("UI/Commands/CopySettings", true);
        private static readonly Texture2D PasteSettingsTex =
            ContentFinder<Texture2D>.Get("UI/Commands/PasteSettings", true);

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (var g in base.GetGizmos())
            {
                if (IsVanillaStorageGizmo(g)) continue;
                yield return g;
            }

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

        /// <summary>
        /// 原版 Building_Storage 的设置类按钮：StorageSettingsClipboard（复制/粘贴设置，
        /// 按图标识别）+ StorageGroupUtility（存储组链接，按翻译标签识别）。
        /// 保留：拆除、禁用、已存物品选择（无害）。
        /// </summary>
        private static bool IsVanillaStorageGizmo(Gizmo g)
        {
            var ca = g as Command_Action;
            if (ca == null) return false;
            if (ca.icon == CopySettingsTex || ca.icon == PasteSettingsTex) return true;
            if (ca.defaultLabel == null) return false;
            string label = ca.defaultLabel;
            return label == "LinkStorageSettings".Translate().ToString()
                || label == "UnlinkStorageSettings".Translate().ToString()
                || label == "CommandCopyZoneSettingsLabel".Translate().ToString()
                || label == "CommandPasteZoneSettingsLabel".Translate().ToString();
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
