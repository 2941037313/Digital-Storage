using UnityEngine;
using Verse;

namespace DigitalStorage.Effects
{
    /// <summary>
    /// <b>代理建筑干活时出现在目标上的那只手</b>（纯表现，零机制）。
    ///
    /// <para>贴图复用「劳务外包手」的爪子（<c>More Organs\1.6\Textures\Things\Projectile\MoreOrgans\Claw_Black.png</c>，
    /// 同一个作者的项目，直接拷进 <c>Textures/2.0/工作手.png</c>）。</para>
    ///
    /// <para><b>为什么是 Mote 而不是别的东西</b>（先例：More Organs <c>LaborHandMote.cs</c>）：</para>
    /// <list type="bullet">
    /// <item><c>Mote</c> 不进 <c>listerThings</c>（<c>ThingCategory.Mote</c> 被 <c>ListerThings.EverListable</c> 排除）、
    /// 不进存档 ⇒ 纯装饰物必须待在这个真空区里；</item>
    /// <item><c>needsMaintenance + fadeOutUnmaintained + fadeOutTime</c> 是一套**自愈**机制：
    /// comp 每 tick 调 <c>Maintain()</c>，一旦没人维护（建筑被拆 / 断电 / 存档里的孤儿），
    /// 它会在 <c>fadeOutTime</c> 之后自己 <c>Destroy()</c>（<c>Verse\Mote.cs:192-199</c>），
    /// 绝不会在地图上留一只永久的手。</item>
    /// </list>
    ///
    /// <para><b>⚠️ 动画必须走 <c>exactPosition</c> / <c>yOffset</c>，覆盖 <c>DrawPos</c> 是无效的</b>：
    /// <c>Mote.DrawAt</c> → <c>DrawMote(altitude)</c> 会把 <c>exactPosition.y</c> 强制设成
    /// <c>altitude + yOffset</c>（<c>Verse\Mote.cs:218-225</c>），然后**直接在 <c>exactPosition</c> 处画**
    /// —— 所以本类在 <c>base.DrawAt</c> 之前临时改 <c>exactPosition.x/z</c> 与 <c>yOffset</c>，画完还原。</para>
    /// </summary>
    public class Mote_DS_WorkHand : Mote
    {
        /// <summary>一次"挥击"的时长（tick）。</summary>
        private const int StrikeTicks = 12;

        /// <summary>挥击时往下砸的幅度（格）。</summary>
        private const float StrikeDip = 0.30f;

        private int lastStrikeTick = -99999;
        private float baseYOffset;

        /// <summary>年龄上限关掉：生死**只**由"还有没有人 Maintain"决定（自愈优先于计时）。</summary>
        protected override bool EndOfLife
        {
            get { return false; }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            baseYOffset = yOffset;
        }

        /// <summary>由 comp 在每次"干完一步"（挖一镐 / 砍一刀 / 建一点）时调用。</summary>
        public void Strike()
        {
            lastStrikeTick = Find.TickManager.TicksGame;
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            int now = Find.TickManager.TicksGame;
            int since = now - lastStrikeTick;

            // 待机呼吸（offsetRandom 让每只手相位不同，多只不会整齐划一）
            float dip = Mathf.Sin((now + offsetRandom) * 0.10f) * 0.045f;
            float dx = 0f;
            float dz = 0f;

            if (since >= 0 && since < StrikeTicks)
            {
                float t = (float)since / StrikeTicks;
                float punch = Mathf.Sin(t * Mathf.PI);   // 0→1→0
                dip -= StrikeDip * punch;                // 往下砸
                dz += 0.10f * punch;                    // 略朝镜头，压在目标正面
                dx += Mathf.Sin(t * Mathf.PI * 2f) * 0.05f;
            }

            yOffset = baseYOffset + dip;
            exactPosition.x += dx;
            exactPosition.z += dz;

            base.DrawAt(drawLoc, flip);

            // 还原：x/z 不还原会逐帧累积漂移（y 由 Mote.DrawMote 每帧重设，不用管）
            exactPosition.x -= dx;
            exactPosition.z -= dz;
        }
    }
}
