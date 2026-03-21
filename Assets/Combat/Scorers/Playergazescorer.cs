using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Penalizes positions that are directly in the player's current gaze direction.
    /// Guards avoid running into where the player is aiming -- realistic and tactical.
    /// Uses StealthTarget velocity and facing to estimate gaze direction.
    /// </summary>
    public class PlayerGazeScorer : TacticalScorerBase
    {
        public override string Name => "PlayerGazeScorer";
        public override float Weight { get; set; } = 1.3f;

        [Range(15f, 60f)] public float GazeConeAngle = 35f;
        [Range(3f, 20f)] public float GazeRange = 12f;

        public override float Score(TacticalSpot spot, TacticalContext ctx)
        {
            // Get player facing from StealthTarget
            var target = HuntDirector.GetTarget();
            if (target == null) return 1f;

            Vector3 playerPos = ctx.EstimatedThreatPos;
            Vector3 playerFwd = ctx.ThreatVelocity.magnitude > 0.3f
                ? ctx.ThreatVelocity.normalized
                : (ctx.UnitPosition - playerPos).normalized; // fallback -- face toward last known unit

            Vector3 toSpot = (spot.Position - playerPos);
            toSpot.y = 0f;
            if (toSpot.magnitude < 0.1f) return 0.5f;

            // Beyond gaze range -- not relevant
            if (toSpot.magnitude > GazeRange) return 1f;

            float angle = Vector3.Angle(playerFwd, toSpot.normalized);

            if (angle < GazeConeAngle)
            {
                // Spot is in player's gaze cone -- penalize heavily
                float t = 1f - angle / GazeConeAngle;
                return 1f - t * 0.8f;
            }

            return 1f;
        }
    }
}