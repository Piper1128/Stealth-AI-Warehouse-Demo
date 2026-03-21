using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Finds flanking positions perpendicular to the threat direction.
    /// Samples NavMesh positions to the left and right of the threat,
    /// preferring positions that are not visible from the threat's current facing.
    /// </summary>
    public class FlankProvider : ITacticalProvider
    {
        public string Tag => "FlankProvider";
        public bool IsEnabled { get; set; } = true;

        [Range(2, 16)] public int SampleCount = 8;
        [Range(4f, 20f)] public float FlankRadius = 10f;
        [Range(2f, 8f)] public float MinFlankDist = 4f;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();
            if (!ctx.Threat.HasIntel) return spots;

            Vector3 toThreat = (ctx.EstimatedThreatPos - ctx.UnitPosition);
            toThreat.y = 0f;
            if (toThreat.magnitude < 0.1f) return spots;

            Vector3 threatDir = toThreat.normalized;
            Vector3 right = Vector3.Cross(threatDir, Vector3.up);
            Vector3 left = -right;

            // Sample positions on both flanks
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)i / (SampleCount - 1); // 0..1

                // Alternate left/right with varying depth
                Vector3 flankDir = (i % 2 == 0) ? right : left;
                float lateral = FlankRadius * (0.5f + t * 0.5f);
                float forward = FlankRadius * t * 0.6f;

                Vector3 candidate = ctx.EstimatedThreatPos
                    + flankDir * lateral
                    + threatDir * forward;

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit,
                    3f, ctx.NavMeshMask)) continue;

                float distToUnit = Vector3.Distance(ctx.UnitPosition, hit.position);
                if (distToUnit < MinFlankDist) continue;
                if (distToUnit > ctx.SearchRadius) continue;

                // Verify path is actually reachable -- no wall cutting
                var path = new NavMeshPath();
                if (!NavMesh.CalculatePath(ctx.UnitPosition, hit.position,
                    ctx.NavMeshMask, path)) continue;
                if (path.status != NavMeshPathStatus.PathComplete) continue;

                var spot = TacticalSpot.FromPosition(hit.position, Tag);
                spot.DistanceToThreat = Vector3.Distance(hit.position, ctx.EstimatedThreatPos);

                // Flank angle -- angle between threat-to-unit and threat-to-spot
                Vector3 threatToUnit = (ctx.UnitPosition - ctx.EstimatedThreatPos).normalized;
                Vector3 threatToSpot = (hit.position - ctx.EstimatedThreatPos).normalized;
                spot.FlankAngle = Vector3.Angle(threatToUnit, threatToSpot);

                // Facing direction -- toward threat from flank position
                spot.FacingDirection = (ctx.EstimatedThreatPos - hit.position).normalized;
                spot.Height = hit.position.y - ctx.UnitPosition.y;

                spots.Add(spot);
            }

            return spots;
        }
    }
}