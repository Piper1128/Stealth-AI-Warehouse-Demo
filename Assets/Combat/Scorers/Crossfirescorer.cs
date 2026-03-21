using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Penalizes positions that put this unit in the line of fire of a teammate.
    /// Guards should never cross their squadmates' firing lines.
    /// Also penalizes positions where the unit would shoot past a teammate.
    /// </summary>
    public class CrossfireScorer : TacticalScorerBase
    {
        public override string Name => "CrossfireScorer";
        public override float Weight { get; set; } = 1.8f;

        [Range(1f, 5f)] public float CrossfireRadius = 2f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            float penalty = 0f;

            for (int i = 0; i < ctx.SquadMembers.Count; i++)
            {
                var member = ctx.SquadMembers[i];
                if (member == null || member == ctx.Unit) continue;

                Vector3 memberPos = member.transform.position;
                Vector3 memberEye = memberPos + Vector3.up * 1.4f;
                Vector3 threatEye = ctx.EstimatedThreatPos + Vector3.up * 1.0f;
                Vector3 spotCenter = spot.Position + Vector3.up * 1.0f;

                // Check if spot is between teammate and threat (in their line of fire)
                Vector3 fireDir = (threatEye - memberEye).normalized;
                Vector3 toSpot = spotCenter - memberEye;
                float proj = Vector3.Dot(toSpot, fireDir);

                if (proj > 0f && proj < toSpot.magnitude)
                {
                    float lateral = Vector3.Cross(fireDir, toSpot).magnitude;
                    if (lateral < CrossfireRadius)
                        penalty += (1f - lateral / CrossfireRadius) * 0.5f;
                }

                // Check minimum separation from teammates
                float separation = Vector3.Distance(spot.Position, memberPos);
                if (separation < 2f)
                    penalty += (1f - separation / 2f) * 0.4f;
            }

            return Mathf.Clamp01(1f - penalty);
        }
    }
}