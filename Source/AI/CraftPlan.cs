using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>面板里的一条"bill"</b> —— 这是 4.0.1 的结构变更：
    /// 配方**不再镜像工作台上已有的 bill**，而是玩家在我们自己的面板里选的（工作台只负责"解锁配方"）。
    ///
    /// <para>所以这里的数据是我们的、存档也是我们的：配方 + 次数模式 + 剩余 + 挂起。</para>
    ///
    /// <para><b>并发</b>（用户拍板）= 范围内"能做这个配方且可用"的工作台数 ⇒
    /// <see cref="lines"/> 就是这个配方当前在产的生产线，每条占一张台子。</para>
    ///
    /// <para><b>为什么不直接用原版 <c>Bill_Production</c> 存</b>：原版的 <c>BillStack</c> 必须挂在
    /// 一个 <c>IBillGiver</c> 上（<c>Bill.Map</c> / <c>DeletedOrDereferenced</c> 都要 <c>billStack.billGiver</c>）。
    /// 而我们的控制面是建筑上的一个 comp，没有"自己的 BillStack 宿主"可用 —— 与其硬造一个假 giver
    /// （会牵出 <c>BillStack.MaxCount</c>、<c>ITab_Bills</c>、Scribe 等一堆原版语义），
    /// 不如把"次数/挂起"这五个字段自己拿着；真正需要原版 bill 的只有**选料**那一步（见 <see cref="BillStackSwap"/>）。</para>
    /// </summary>
    internal sealed class CraftPlan : IExposable
    {
        /// <summary>次数模式：无限。</summary>
        public const int ModeForever = 0;

        /// <summary>次数模式：做满 N 件就停。</summary>
        public const int ModeCount = 1;

        public RecipeDef recipe;

        /// <summary>见 <see cref="ModeForever"/> / <see cref="ModeCount"/>。</summary>
        public int mode = ModeForever;

        /// <summary>次数模式下的剩余件数（只减不增，除了玩家点 +1/+10）。</summary>
        public int remaining;

        public bool suspended;

        /// <summary>这个配方累计做出来多少件（面板显示用）。</summary>
        public int completed;

        /// <summary>当前在产的生产线（每条 = 一张台子）。**不进存档**：读档后按可用台子重建。</summary>
        public readonly List<CraftLine> lines = new List<CraftLine>();

        public bool Done
        {
            get { return mode == ModeCount && remaining <= 0; }
        }

        public bool Active
        {
            get { return !suspended && !Done && recipe != null; }
        }

        /// <summary>玩家点 +N：确保是次数模式并把剩余加上去。</summary>
        public void AddCount(int n)
        {
            if (mode != ModeCount)
            {
                mode = ModeCount;
                remaining = 0;
            }
            remaining += n;
            if (remaining < 0) remaining = 0;
            suspended = false;
        }

        /// <summary>切成无限模式。</summary>
        public void SetForever()
        {
            mode = ModeForever;
            remaining = 0;
            suspended = false;
        }

        /// <summary>做完一件的记账（**唯一入口**，别在别处改 remaining）。</summary>
        public void NoteCompleted()
        {
            completed++;
            if (mode == ModeCount && remaining > 0) remaining--;
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref recipe, "recipe");
            Scribe_Values.Look(ref mode, "mode", ModeForever);
            Scribe_Values.Look(ref remaining, "remaining", 0);
            Scribe_Values.Look(ref suspended, "suspended", false);
            Scribe_Values.Look(ref completed, "completed", 0);
            // lines 刻意不存：在产的活是派生状态，扣料只在完成那一刻 ⇒ 读档丢进度不丢料。
        }
    }
}
