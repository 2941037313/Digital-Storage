using System;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 账本分组键：只按 def + stuff 区分。
    /// 不含品质、耐久——那些物品走不进账本（LedgerPolicy.CanIngest 拒绝）。
    /// </summary>
    public struct ItemKey : IEquatable<ItemKey>
    {
        public readonly ThingDef def;
        public readonly ThingDef stuff;

        public ItemKey(ThingDef def, ThingDef stuff)
        {
            this.def = def;
            this.stuff = stuff;
        }

        public static ItemKey Of(Thing t) => new ItemKey(t.def, t.Stuff);

        public bool Equals(ItemKey other) => def == other.def && stuff == other.stuff;
        public override bool Equals(object obj) => obj is ItemKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = def != null ? def.GetHashCode() : 0;
                h = (h * 397) ^ (stuff != null ? stuff.GetHashCode() : 0);
                return h;
            }
        }

        public override string ToString()
        {
            if (def == null) return "(null)";
            return stuff != null ? $"{stuff.LabelAsStuff} {def.label}" : def.label;
        }

        /// <summary>
        /// 存档键格式："defName|stuffDefName"（stuff 为 null 时写 "-"）。
        /// </summary>
        public string ToSaveString()
        {
            string d = def != null ? def.defName : "?";
            string s = stuff != null ? stuff.defName : "-";
            return d + "|" + s;
        }

        public static bool TryParse(string raw, out ItemKey key)
        {
            key = default;
            if (string.IsNullOrEmpty(raw)) return false;
            int bar = raw.IndexOf('|');
            if (bar < 0) return false;

            string d = raw.Substring(0, bar);
            string s = raw.Substring(bar + 1);

            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(d);
            if (def == null) return false;
            ThingDef stuff = s == "-" ? null : DefDatabase<ThingDef>.GetNamedSilentFail(s);

            key = new ItemKey(def, stuff);
            return true;
        }

        /// <summary>
        /// #4: 消除 JobDriver 里的 Scribe 模板重复。
        /// Saves: "keyLabel"=defName|stuff, "countLabel"=int
        /// Loads: 还原 ItemKey 和 count。
        /// </summary>
        public static void Scribe_KeyAndCount(ref ItemKey key, ref int count,
            string keyLabel, string countLabel)
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                string s = (key.def != null) ? key.ToSaveString() : null;
                Scribe_Values.Look(ref s, keyLabel);
                Scribe_Values.Look(ref count, countLabel);
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                string s = null;
                Scribe_Values.Look(ref s, keyLabel);
                Scribe_Values.Look(ref count, countLabel);
                if (s != null) TryParse(s, out key);
            }
        }
    }
}
