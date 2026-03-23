using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Scores how well a spot protects the unit from the threat.
    /// Raycasts from spot toward threat -- if blocked, spot has cover.
    /// Also checks bullet penetration by measuring wall thickness.
    /// </summary>
    public class CoverQualityScorer : TacticalScorerBase
    {
        public override string Name => "CoverQualityScorer";
        public override float Weight { get; set; } = 2.0f;

        [Range(0f, 1f)] public float PenetrationThreshold = 0.3f; // walls thinner than this fail

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            Vector3 spotEye = spot.Position + Vector3.up * 1.4f;
            Vector3 threatEye = ctx.EstimatedThreatPos + Vector3.up * 1.0f;
            Vector3 dir = (threatEye - spotEye).normalized;
            float dist = Vector3.Distance(spotEye, threatEye);

            // No cover geometry -- open ground
            if (!Physics.Raycast(spotEye, dir, out RaycastHit frontHit, dist))
                return 0.05f;

            // Has cover -- measure wall thickness for penetration check
            float wallThickness = 0f;
            if (Physics.Raycast(threatEye, -dir, out RaycastHit backHit, dist))
            {
                wallThickness = Vector3.Distance(frontHit.point, backHit.point);
            }

            float coverScore = 0.7f; // base score for any cover

            // Bonus for thick walls
            coverScore += Mathf.Clamp01(wallThickness / 1.5f) * 0.3f;

            // Penalty for thin walls that bullets can penetrate
            if (wallThickness < PenetrationThreshold)
                coverScore *= 0.3f;

            // Bonus for hard cover tagged objects
            if (spot.HasHardCover) coverScore = Mathf.Min(1f, coverScore + 0.15f);

            return Mathf.Clamp01(coverScore);
        }
    }
}