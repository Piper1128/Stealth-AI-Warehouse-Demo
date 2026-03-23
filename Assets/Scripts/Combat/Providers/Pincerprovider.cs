using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Generates coordinated pincer positions -- spots that together
    /// form a two-pronged attack on the estimated threat position.
    ///
    /// Units using this provider approach the threat from opposite angles,
    /// forcing the player to choose which threat to face.
    ///
    /// Coordination: each unit gets a different pincer arm based on squad index.
    /// Unit 0 (even)  left arm. Unit 1 (odd)  right arm.
    /// </summary>
    public class PincerProvider : ITacticalProvider
    {
        public string Tag => "PincerProvider";
        public bool IsEnabled { get; set; } = true;

        [Range(30f, 90f)] public float PincerAngle = 60f;  // degrees each arm sweeps
        [Range(4f, 20f)] public float PincerRadius = 10f;
        [Range(2, 8)] public int SampleCount = 4;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();
            if (!ctx.Threat.HasIntel) return spots;
            if (ctx.SquadMembers.Count < 2) return spots;

            // Determine which arm this unit takes
            int unitIndex = ctx.SquadMembers.IndexOf(ctx.Unit);
            bool isLeftArm = unitIndex % 2 == 0;

            Vector3 toUnit = (ctx.UnitPosition - ctx.EstimatedThreatPos);
            toUnit.y = 0f;
            if (toUnit.magnitude < 0.1f) return spots;

            float baseAngle = Mathf.Atan2(toUnit.x, toUnit.z) * Mathf.Rad2Deg;
            float armSign = isLeftArm ? -1f : 1f;

            // Sample spots along this arm
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)(i + 1) / SampleCount;
                float angle = baseAngle + armSign * PincerAngle * t;
                float radius = PincerRadius * (0.6f + t * 0.4f);

                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                Vector3 candidate = ctx.EstimatedThreatPos + dir * radius;

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit,
                    3f, ctx.NavMeshMask)) continue;

                float distToUnit = Vector3.Distance(ctx.UnitPosition, hit.position);
                if (distToUnit > ctx.SearchRadius) continue;

                // Verify reachable
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(ctx.UnitPosition, hit.position,
                    ctx.NavMeshMask, path)) continue;
                if (path.status != NavMeshPathStatus.PathComplete) continue;

                var spot = TacticalSpot.FromPosition(hit.position, Tag);
                spot.DistanceToThreat = Vector3.Distance(hit.position, ctx.EstimatedThreatPos);
                spot.FacingDirection = (ctx.EstimatedThreatPos - hit.position).normalized;

                // High flank angle -- this is the whole point of pincer
                Vector3 threatToUnit = (ctx.UnitPosition - ctx.EstimatedThreatPos).normalized;
                Vector3 threatToSpot = (hit.position - ctx.EstimatedThreatPos).normalized;
                spot.FlankAngle = Vector3.Angle(threatToUnit, threatToSpot);

                spots.Add(spot);
            }

            return spots;
        }
    }
}