using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Scores positions based on their flanking angle relative to the threat.
    /// A pure flank (90 degrees) scores highest.
    /// A rear attack (180 degrees) scores very high.
    /// A direct frontal approach (0 degrees) scores lowest.
    /// </summary>
    public class FlankAngleScorer : TacticalScorerBase
    {
        public override string Name => "FlankAngleScorer";
        public override float Weight { get; set; } = 1.5f;

        [Range(60f, 120f)] public float IdealFlankAngle = 90f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            // FlankAngle is angle between threat-to-unit and threat-to-spot
            // 0   = same direction as current unit (no improvement)
            // 90  = true flank
            // 180 = rear attack
            float angle = spot.FlankAngle;

            if (angle < 30f) return 0.1f; // same direction, no value
            if (angle > 150f) return 0.9f; // rear attack -- very valuable

            // Peak at ideal flank angle
            return ScoreByDistance(angle, IdealFlankAngle, 50f);
        }
    }
}