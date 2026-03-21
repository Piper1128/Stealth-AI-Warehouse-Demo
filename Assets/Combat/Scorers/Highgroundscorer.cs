using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Scores positions based on height advantage over the threat.
    /// Guards prefer elevated positions -- better LOS, harder to hit.
    /// Penalizes positions below the threat (bad tactical ground).
    /// </summary>
    public class HighGroundScorer : TacticalScorerBase
    {
        public override string Name => "HighGroundScorer";
        public override float Weight { get; set; } = 1.0f;

        [Range(0.5f, 4f)] public float IdealHeightGain = 2f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            float threatHeight = ctx.EstimatedThreatPos.y;
            float spotHeight = spot.Position.y;
            float heightDelta = spotHeight - threatHeight;

            if (heightDelta < -1f)
                // Below threat -- significant penalty
                return Mathf.Clamp01(0.2f + (heightDelta + 1f) * 0.1f);

            if (heightDelta < 0f)
                // Slightly below -- minor penalty
                return 0.3f;

            // Above threat -- reward
            return Mathf.Clamp01(0.4f + Normalize(heightDelta, 0f, IdealHeightGain) * 0.6f);
        }
    }
}