using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Scores how much a spot advances toward the threat.
    /// Spots closer to threat than current unit position score higher.
    /// Creates aggressive forward pressure -- guards don't camp forever.
    /// </summary>
    public class AdvanceScorer : TacticalScorerBase
    {
        public override string Name => "AdvanceScorer";
        public override float Weight { get; set; } = 1.5f;

        [Range(3f, 20f)] public float IdealEngageDist = 8f;  // sweet spot distance from threat
        [Range(2f, 10f)] public float Falloff = 6f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            float currentDist = Vector3.Distance(ctx.UnitPosition, ctx.EstimatedThreatPos);
            float spotDist = spot.DistanceToThreat;

            // Reward spots that are closer to ideal engagement distance
            float distScore = ScoreByDistance(spotDist, IdealEngageDist, Falloff);

            // Bonus if spot is closer to threat than current position
            float advanceBonus = spotDist < currentDist ? 0.2f : 0f;

            // Penalty if already very close -- don't suicide rush
            float rushPenalty = spotDist < 2f ? 0.5f : 1f;

            return Mathf.Clamp01((distScore + advanceBonus) * rushPenalty);
        }
    }
}