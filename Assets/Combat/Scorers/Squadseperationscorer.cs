using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Penalizes positions too close to other squad members.
    /// Guards should spread out -- stacking makes them vulnerable to grenades
    /// and reduces their tactical coverage.
    /// </summary>
    public class SquadSeparationScorer : TacticalScorerBase
    {
        public override string Name => "SquadSeparationScorer";
        public override float Weight { get; set; } = 1.2f;

        [Range(2f, 10f)] public float IdealSeparation = 5f;
        [Range(1f, 4f)] public float MinSeparation = 2f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            float minDist = float.MaxValue;

            for (int i = 0; i < ctx.SquadMembers.Count; i++)
            {
                var member = ctx.SquadMembers[i];
                if (member == null || member == ctx.Unit) continue;

                float d = Vector3.Distance(spot.Position, member.transform.position);
                if (d < minDist) minDist = d;
            }

            if (minDist == float.MaxValue) return 1f; // no teammates -- free choice

            // Below minimum -- heavy penalty
            if (minDist < MinSeparation)
                return minDist / MinSeparation * 0.2f;

            // Between min and ideal -- linear ramp
            if (minDist < IdealSeparation)
                return Normalize(minDist, MinSeparation, IdealSeparation) * 0.8f + 0.2f;

            // Beyond ideal -- full score
            return 1f;
        }
    }
}