using System;
using DigitalStorage.Components;
using UnityEngine;
using Verse;

namespace DigitalStorage.Effects
{
    /// <summary>
    /// 自动收纳的"光束"特效：物品上打下一道光柱 → 光柱消散时物品才真正消失（入库）。
    ///
    /// <para><b>外观抄自米莉拉（Milira Race）的米莉安空降</b>
    /// （<c>Milira/CompLightBeam.cs</c>）：同样用原版打包贴图
    /// <c>Other/OrbitalBeam</c> + <c>Other/OrbitalBeamEnd</c>、同样用
    /// <c>ShaderDatabase.MoteGlow</c> 与 <c>MapMaterialRenderQueues.OrbitalBeam</c>、
    /// 同样用 <c>Graphics.DrawMesh</c> + <c>MaterialPropertyBlock</c> 逐帧画。
    /// <b>没有引入任何第三方素材</b>（那两张贴图是原版资源，米莉拉自己也没带）。</para>
    ///
    /// <para><b>与米莉拉的差别</b>：它画的是一条<b>贴地平扫到地图边缘</b>的长光束
    /// （空降建筑用），我们画的是<b>立在物品上方的光柱</b>（十字交叉两片，从任意角度看都像柱体）
    /// 加地面落点光斑 —— "被吸走"这个语义更直白。想要它那种贴地长光束，
    /// 改 <c>DrawAt</c> 里的旋转与偏移即可。</para>
    ///
    /// <para><b>为什么用 Mote 而不是 Thing+Comp</b>：<c>ListerThings.EverListable</c>
    /// 明确<b>不登记</b> <c>ThingCategory.Mote</c>（<c>ListerThings.cs:300</c>）⇒ 它不进
    /// <c>listerThings</c>、不进存档、不会被我们自己的
    /// <c>ThingsInGroup(HaulableEver)</c> 扫到、也不参与任何容器语义。
    /// 纯装饰物必须待在这个真空区里。</para>
    ///
    /// <para><b>⚠️ 正确性：入库是延后执行的，所以必须能安全放弃。</b>
    /// 物品在光束期间**仍留在地上**（视觉上就是"光束打下来 → 然后消失"），
    /// 因此它可能被小人搬走、被吃掉、被烧掉。收尾时只在
    /// <c>target.Spawned &amp;&amp; ParentHolder == null</c>（还在原地、仍是地图上的散落物）
    /// 才入库；否则放弃本次 —— <b>物品绝不会丢</b>，下一轮扫描会再收。</para>
    /// </summary>
    [StaticConstructorOnStartup]
    public class Mote_DS_IngestBeam : Mote
    {
        /// <summary>整个特效的时长（tick）。消散完成后才入库。</summary>
        public const int TotalDuration = 45;

        /// <summary>结束时的淡出时长。</summary>
        private const int FadeOutDuration = 18;

        /// <summary>光柱拔起的时长。</summary>
        private const int GrowTicks = 12;

        private const float MaxHeight = 8f;
        private const float BeamWidth = 0.4f;
        private const float FootSize = 1.2f;

        private static readonly Material BeamMat = MaterialPool.MatFrom(
            "Other/OrbitalBeam", ShaderDatabase.MoteGlow, MapMaterialRenderQueues.OrbitalBeam);

        private static readonly Material BeamEndMat = MaterialPool.MatFrom(
            "Other/OrbitalBeamEnd", ShaderDatabase.MoteGlow, MapMaterialRenderQueues.OrbitalBeam);

        private static readonly MaterialPropertyBlock MatPropertyBlock = new MaterialPropertyBlock();

        private static readonly Color BeamColor = new Color(0.55f, 0.9f, 1f, 0.85f);

        private Thing target;
        private Building_StorageCore core;
        private int startTick;
        private float angle;
        private bool finished;

        public void Init(Building_StorageCore core, Thing target, float angle)
        {
            this.core = core;
            this.target = target;
            this.angle = angle;
            startTick = Find.TickManager.TicksGame;
        }

        private int TicksPassed => Find.TickManager.TicksGame - startTick;

        private int TicksLeft => TotalDuration - TicksPassed;

        /// <summary>销毁时机完全由 <see cref="Finish"/> 掌握，不看 Def 的 lifespan
        /// （基类 <c>Mote.TimeInterval</c> 一旦判定 EndOfLife 就会直接 Destroy，
        /// 那样就没有机会执行入库了）。</summary>
        protected override bool EndOfLife => false;

        protected override void Tick()
        {
            if (finished)
            {
                if (!Destroyed) Destroy();
                return;
            }
            base.Tick();
            if (TicksPassed >= TotalDuration) Finish();
        }

        private void Finish()
        {
            finished = true;
            try
            {
                // 只在"还在原地、仍是地图上的散落物"时才入库。
                // 半路被小人搬走 / 被吃掉 / 被烧掉 ⇒ 放弃本次（物品不丢，下轮再收）。
                if (target != null && !target.Destroyed && target.Spawned && target.ParentHolder == null
                    && core != null && !core.Destroyed)
                {
                    CompAutoIngest.TryIngest(core, target);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 收纳光束收尾时入库失败（物品留在原地）: " + e, 0x5D51B);
            }
            finally
            {
                CompAutoIngest.ClearBeaming(target);
                if (!Destroyed) Destroy();
            }
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (TicksLeft <= 0 || Find.UIRoot.HideMotes) return;

            float grow = Mathf.Clamp01((float)TicksPassed / GrowTicks);
            float height = MaxHeight * grow;

            float alpha = 0.92f + Mathf.Sin((float)TicksPassed * 0.35f) * 0.08f;
            if (TicksLeft < FadeOutDuration)
            {
                alpha *= (float)TicksLeft / FadeOutDuration;
            }
            Color color = BeamColor;
            color.a *= alpha;
            MatPropertyBlock.SetColor(ShaderPropertyIDs.Color, color);

            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();

            // 光柱：两片互相垂直的竖直面（十字），从任意角度看都像柱体。
            if (height > 0.05f)
            {
                Vector3 center = new Vector3(exactPosition.x, altitude + height * 0.5f, exactPosition.z);
                for (int i = 0; i < 2; i++)
                {
                    Matrix4x4 m = default(Matrix4x4);
                    m.SetTRS(center,
                        Quaternion.Euler(0f, angle + i * 90f, 0f) * Quaternion.Euler(90f, 0f, 0f),
                        new Vector3(BeamWidth, 1f, height));
                    Graphics.DrawMesh(MeshPool.plane10, m, BeamMat, 0, null, 0, MatPropertyBlock);
                }
            }

            // 地面落点光斑（对应米莉拉那条的 BeamEnd）。
            Matrix4x4 foot = default(Matrix4x4);
            float footSize = FootSize * grow;
            foot.SetTRS(new Vector3(exactPosition.x, altitude, exactPosition.z),
                Quaternion.Euler(0f, angle, 0f), new Vector3(footSize, 1f, footSize));
            Graphics.DrawMesh(MeshPool.plane10, foot, BeamEndMat, 0, null, 0, MatPropertyBlock);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref target, "dsBeamTarget");
            Scribe_References.Look(ref core, "dsBeamCore");
            Scribe_Values.Look(ref startTick, "dsBeamStart", 0);
            Scribe_Values.Look(ref angle, "dsBeamAngle", 0f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && (target == null || core == null))
            {
                // 装饰物而已，目标没了就自我了断（入库的那件东西还在原地，不受影响）。
                finished = true;
            }
        }
    }
}
