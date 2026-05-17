using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 共享食物评分，避免 ThingMaker 分配。
    /// 复刻 FoodUtility.FoodOptimality 但无距离/腐烂依赖。
    /// </summary>
    public static class FoodScoring
    {
        public static float Score(Pawn eater, ThingDef def)
        {
            if (def.ingestible == null) return -9999f;

            float score = 300f;

            switch (def.ingestible.preferability)
            {
                case FoodPreferability.NeverForNutrition: return -9999f;
                case FoodPreferability.DesperateOnly: score -= 150f; break;
                case FoodPreferability.DesperateOnlyForHumanlikes:
                    if (eater.RaceProps.Humanlike) score -= 150f;
                    break;
            }

            if (eater.RaceProps.Humanlike)
                score += def.ingestible.optimalityOffsetHumanlikes;
            else if (eater.RaceProps.Animal)
                score += def.ingestible.optimalityOffsetFeedingAnimals;

            // Trait 偏好
            if (eater.story?.traits != null && eater.story.traits.AnyTraitHasIngestibleOverrides)
            {
                var allTraits = eater.story.traits.allTraits;
                for (int i = 0; i < allTraits.Count; i++)
                {
                    if (allTraits[i].Suppressed) continue;
                    var mods = allTraits[i].CurrentData.ingestibleModifiers;
                    if (mods.NullOrEmpty()) continue;
                    for (int j = 0; j < mods.Count; j++)
                    {
                        if (mods[j].ingestible == def)
                            score += mods[j].optimalityOffset;
                    }
                }
            }

            // Mood penalty via FoodUtility.ThoughtsFromIngesting requires a Thing.
            // Use FoodUtility.FoodOptimality with a zero-distance fix to get mood effect.
            // But that also needs a Thing. So we approximate:
            // Humanlike meat → ~-20 mood for non-cannibal
            if (eater.RaceProps.Humanlike && eater.needs?.mood != null)
            {
                bool isCannibal = DefDatabase<TraitDef>.GetNamedSilentFail("Cannibal") is TraitDef cd
                    && eater.story?.traits?.HasTrait(cd) == true;
                bool isAscetic = DefDatabase<TraitDef>.GetNamedSilentFail("Ascetic") is TraitDef ad
                    && eater.story?.traits?.HasTrait(ad) == true;

                // Meat_Human / Meat_HumanSurprise → ~ -20 mood (AteHumanlikeMeatDirect)
                if (def.defName.Contains("Human"))
                    score += EvalMoodCurve(isCannibal ? 10f : -20f);

                // Raw food for non-ascetic non-cannibal → ~ -7 mood
                if (def.IsRawHumanFood() && !isAscetic && !isCannibal)
                    score += EvalMoodCurve(-7f);
            }

            return score;
        }

        private static float EvalMoodCurve(float moodEffect)
        {
            if (moodEffect <= -100f) return -600f;
            if (moodEffect <= -10f) return Lerp(-100f, -10f, -600f, -100f, moodEffect);
            if (moodEffect <= -5f) return Lerp(-10f, -5f, -100f, -70f, moodEffect);
            if (moodEffect <= -1f) return Lerp(-5f, -1f, -70f, -50f, moodEffect);
            if (moodEffect <= 0f) return Lerp(-1f, 0f, -50f, 0f, moodEffect);
            if (moodEffect >= 100f) return 800f;
            return Lerp(0f, 100f, 0f, 800f, moodEffect);
        }

        private static float Lerp(float x0, float x1, float y0, float y1, float x)
        {
            return y0 + (y1 - y0) * ((x - x0) / (x1 - x0));
        }
    }
}
