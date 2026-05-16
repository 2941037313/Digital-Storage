using System.Collections.Generic;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    public class CoreDestroyDropQueue : MapComponent
    {
        private struct DropEntry
        {
            public ItemKey key;
            public long amount;
            public IntVec3 center;
        }

        private readonly Queue<DropEntry> queue = new Queue<DropEntry>();
        private const int MaxPerFrame = 200;
        private const int MaxTotalEntries = 30000;
        private int totalEnqueued;
        private int totalDiscarded;

        public CoreDestroyDropQueue(Map map) : base(map) { }

        public void Enqueue(ItemKey key, long amount, IntVec3 center)
        {
            if (amount <= 0) return;

            // I6c: 超 3 万条目丢弃
            int stacks = (int)((amount + key.def.stackLimit - 1) / key.def.stackLimit);
            if (totalEnqueued + stacks > MaxTotalEntries)
            {
                int allowed = MaxTotalEntries - totalEnqueued;
                if (allowed <= 0)
                {
                    totalDiscarded += stacks;
                    return;
                }
                long allowedAmount = (long)allowed * key.def.stackLimit;
                totalDiscarded += stacks - allowed;
                amount = allowedAmount;
                stacks = allowed;
            }

            queue.Enqueue(new DropEntry { key = key, amount = amount, center = center });
            totalEnqueued += stacks;
        }

        public override void MapComponentTick()
        {
            if (queue.Count == 0)
            {
                if (totalDiscarded > 0)
                {
                    Find.LetterStack.ReceiveLetter(
                        "DS_CoreDestroyLossTitle".Translate(),
                        "DS_CoreDestroyLossDesc".Translate(totalDiscarded),
                        LetterDefOf.NegativeEvent);
                    totalDiscarded = 0;
                    totalEnqueued = 0;
                }
                return;
            }

            int dropped = 0;
            while (queue.Count > 0 && dropped < MaxPerFrame)
            {
                var entry = queue.Dequeue();
                long remaining = entry.amount;

                while (remaining > 0 && dropped < MaxPerFrame)
                {
                    int stackSize = (int)System.Math.Min(remaining, entry.key.def.stackLimit);
                    Thing thing = ThingMaker.MakeThing(entry.key.def, entry.key.stuff);
                    thing.stackCount = stackSize;
                    GenPlace.TryPlaceThing(thing, entry.center, map, ThingPlaceMode.Near);
                    remaining -= stackSize;
                    dropped++;
                }

                // 没掉完的放回队列
                if (remaining > 0)
                    queue.Enqueue(new DropEntry { key = entry.key, amount = remaining, center = entry.center });
            }
        }
    }
}
