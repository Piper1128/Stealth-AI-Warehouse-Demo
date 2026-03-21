namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Evaluates a tactical spot and returns a score (0-1).
    /// Scorers are the "brain" of the tactical system -- they define
    /// what makes a good position for a given unit and situation.
    ///
    /// Scorers run in parallel via Unity Jobs + Burst when possible.
    /// Keep Score() pure -- no side effects, no Unity API calls.
    ///
    /// Final spot score = sum(scorer.Score(spot, ctx) * scorer.Weight)
    ///                  / sum(scorer.Weight)
    /// </summary>
    public interface ITacticalScorer
    {
        /// <summary>
        /// Unique name shown in Tactical Inspector scorer breakdown.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// How much this scorer influences the final score.
        /// Higher weight = more influence. Typical range: 0.5 - 3.0.
        /// </summary>
        float Weight { get; set; }

        /// <summary>
        /// Whether this scorer is active. Inactive scorers contribute 0.
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// Score this spot (0 = worst, 1 = best).
        /// Must be pure -- no state changes, no Unity API calls.
        /// Context provides everything needed for evaluation.
        /// </summary>
        float Score(TacticalSpot spot, TacticalContext ctx);
    }

    /// <summary>
    /// Base class with sensible defaults. Inherit for quick scorer implementation.
    /// </summary>
    public abstract class TacticalScorerBase : ITacticalScorer
    {
        public abstract string Name { get; }
        public virtual float Weight { get; set; } = 1f;
        public virtual bool IsEnabled { get; set; } = true;

        public abstract float Score(TacticalSpot spot, TacticalContext ctx);

        /// <summary>Normalize a value to 0-1 range.</summary>
        protected static float Normalize(float value, float min, float max)
        {
            if (max <= min) return 0f;
            return UnityEngine.Mathf.Clamp01((value - min) / (max - min));
        }

        /// <summary>Invert a 0-1 score (1 - score).</summary>
        protected static float Invert(float score) => 1f - score;

        /// <summary>
        /// Score based on distance -- closer to idealDist = 1, falls off beyond falloff.
        /// </summary>
        protected static float ScoreByDistance(float dist, float idealDist, float falloff)
        {
            float delta = UnityEngine.Mathf.Abs(dist - idealDist);
            return UnityEngine.Mathf.Clamp01(1f - delta / falloff);
        }
    }
}