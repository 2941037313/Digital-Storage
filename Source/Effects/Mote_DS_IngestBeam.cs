using UnityEngine;
using Verse;

namespace DigitalStorage.Effects
{
    /// <summary>
    /// 自动收纳的"光束"**余像**：物品在这束光出现之前**已经进了核心**，
    /// 这里只是把它原来站的那一格点亮一下。
    ///
    /// <para><b>外观抄自米莉拉（Milira Race）的米莉安空降</b>
    /// （<c>Milira/CompLightBeam.cs</c>）：同样用原版打包贴图
    /// <c>Other/OrbitalBeam</c> + <c>Other/OrbitalBeamEnd</c>、同样用
    /// <c>ShaderDatabase.MoteGlow</c> 与 <c>MapMaterialRenderQueues.OrbitalBeam</c>、
    /// 同样用 <c>Graphics.DrawMesh</c> + <c>MaterialPropertyBlock</c> 逐帧画。
    /// <b>没有引入任何第三方素材</b>（那两张贴图是原版资源，米莉拉自己也没带）。</para>
    ///
    /// <para><b>与米莉拉的差别</b>：它画的是一条<b>贴地平扫到地图边缘</b>的长光束
    /// （空降建筑用），我们画的是<b>立在原地的一根十字光柱</b>（从任意角度看都像柱体）
    /// 加地面落点光斑。想要它那种贴地长光束，改 <c>DrawAt</c> 里的旋转与偏移即可。</para>
    ///
    /// <para><b>为什么用 Mote 而不是 Thing+Comp</b>：<c>ListerThings.EverListable</c>
    /// 明确<b>不登记</b> <c>ThingCategory.Mote</c>（<c>ListerThings.cs:300</c>）⇒ 它不进
    /// <c>listerThings</c>、不进存档、不会被我们自己的
    /// <c>ThingsInGroup(HaulableEver)</c> 扫到、也不参与任何容器语义。
    /// 纯装饰物必须待在这个真空区里。</para>
    ///
    /// <para><b>关于副作用</b>：本类<b>不做任何逻辑</b> —— 入库在 <c>CompAutoIngest</c> 里
    /// 已经当场完成了。所以它被打断、被卸载、存档时消失，都不影响正确性；
    /// 时长与淡入淡出也全部交给 Def 的 <c>mote</c> 字段（基类 <c>Mote.Alpha</c> /
    /// <c>EndOfLife</c> 自己处理），本类只负责画。
    /// 这与"把入库推迟到光束结束"相比省掉了整套防护（目标中途被搬走/销毁就只能放弃）。
    /// </para>
    /// </summary>
    [StaticConstructorOnStartup]
    public class Mote_DS_IngestBeam : Mote
    {
        /// <summary>光柱拔起所需 tick（纯观感；由 HTML 预览定稿：40）。</summary>
        private const int GrowTicks = 40;

        /// <summary>地面落点扇形的存活 tick（由预览定稿：41）。</summary>
        private const int FootDuration = 41;

        private const float MaxHeight = 14f;
        private const float BeamWidth = 1.15f;
        private const float FootSize = 0.8f;

        private static readonly Material BeamMat = MaterialPool.MatFrom(
            "Other/OrbitalBeam", ShaderDatabase.MoteGlow, MapMaterialRenderQueues.OrbitalBeam);

        private static readonly Material BeamEndMat = MaterialPool.MatFrom(
            "Other/OrbitalBeamEnd", ShaderDatabase.MoteGlow, MapMaterialRenderQueues.OrbitalBeam);

        private static readonly MaterialPropertyBlock MatPropertyBlock = new MaterialPropertyBlock();

        /// <summary>HSL(246,100%,70%) × 强度 0.65 —— 由 HTML 预览定稿的紫蓝色。</summary>
        private static readonly Color BeamColor = new Color(0.54f, 0.50f, 0.90f, 0.65f);

        private float angle;

        public void Init(float angle)
        {
            this.angle = angle;
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            if (Destroyed || Find.UIRoot.HideMotes) return;

            int ticksPassed = Find.TickManager.TicksGame - spawnTick;
            float grow = Mathf.Clamp01((float)ticksPassed / GrowTicks);
            float height = MaxHeight * grow;

            // 淡入/淡出/稳定期全部由 Def 的 mote 字段驱动（基类 Alpha 已经算好）。
            float breathe = 0.92f + Mathf.Sin(ticksPassed * 0.35f) * 0.08f;
            float alpha = Alpha * breathe;
            if (alpha <= 0.01f) return;

            float altitude = AltitudeLayer.MetaOverlays.AltitudeFor();

            // 光柱：两片互相垂直的竖直面（十字），从任意角度看都像柱体。
            if (height > 0.05f)
            {
                Color color = BeamColor;
                color.a *= alpha;
                MatPropertyBlock.SetColor(ShaderPropertyIDs.Color, color);

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

            // 地面落点扇形（对应米莉拉那条的 BeamEnd）：只活 FootDuration tick，之后自行淡掉。
            float footGrow = Mathf.Clamp01((float)ticksPassed / GrowTicks);
            if (FootSize > 0f && ticksPassed < FootDuration)
            {
                float footFade = 1f - (float)ticksPassed / FootDuration;
                Color footColor = BeamColor;
                footColor.a *= Alpha * breathe * footFade;
                MatPropertyBlock.SetColor(ShaderPropertyIDs.Color, footColor);

                Matrix4x4 foot = default(Matrix4x4);
                float footSize = FootSize * footGrow;
                foot.SetTRS(new Vector3(exactPosition.x, altitude, exactPosition.z),
                    Quaternion.Euler(0f, angle, 0f), new Vector3(footSize, 1f, footSize));
                Graphics.DrawMesh(MeshPool.plane10, foot, BeamEndMat, 0, null, 0, MatPropertyBlock);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref angle, "dsBeamAngle", 0f);
        }
    }
}
