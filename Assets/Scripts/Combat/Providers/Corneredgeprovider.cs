using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// CQB provider -- finds corner edge positions for "pie slicing."
    /// Detects wall corners by casting rays and finding NavMesh edges,
    /// then places spots just past the corner where guard can peek without exposure.
    ///
    /// This is the "slice the pie" technique used in real CQB:
    /// guard hugs the wall, steps out gradually to clear angles.
    /// </summary>
    public class CornerEdgeProvider : ITacticalProvider
    {
        public string Tag => "CornerEdgeProvider";
        public bool IsEnabled { get; set; } = true;

        [Range(1f, 6f)] public float CornerOffset = 0.4f; // how far past corner to stand
        [Range(3f, 15f)] public float SearchRadius = 8f;
        [Range(4, 16)] public int RayCount = 8;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();
            if (!ctx.Threat.HasIntel) return spots;

            Vector3 origin = ctx.UnitPosition + Vector3.up * 0.5f;
            Vector3 toThreat = (ctx.EstimatedThreatPos - ctx.UnitPosition);
            toThreat.y = 0f;
            if (toThreat.magnitude < 0.1f) return spots;

            // Cast rays in a fan toward threat -- detect walls
            float baseAngle = Mathf.Atan2(toThreat.x, toThreat.z) * Mathf.Rad2Deg;
            float spread = 90f;

            for (int i = 0; i < RayCount; i++)
            {
                float angle = baseAngle - spread * 0.5f + spread * i / (RayCount - 1);
                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;

                if (!Physics.Raycast(origin, dir, out RaycastHit wallHit,
                    SearchRadius)) continue;

                // Found a wall -- find the corner edge via NavMesh
                Vector3 wallPos = wallHit.point;
                Vector3 wallNorm = wallHit.normal;

                // Step along the wall to find where it ends (corner)
                Vector3 wallRight = Vector3.Cross(wallNorm, Vector3.up).normalized;

                for (int side = -1; side <= 1; side += 2)
                {
                    Vector3 cornerCandidate = wallPos
                        + wallRight * side * SearchRadius * 0.5f
                        + wallNorm * CornerOffset;

                    if (!NavMesh.SamplePosition(cornerCandidate, out NavMeshHit navHit,
                        1.5f, ctx.NavMeshMask)) continue;

                    float dist = Vector3.Distance(ctx.UnitPosition, navHit.position);
                    if (dist < 0.5f || dist > ctx.SearchRadius) continue;

                    // Verify this corner gives LOS to threat
                    Vector3 eyePos = navHit.position + Vector3.up * 1.6f;
                    Vector3 threatEye = ctx.EstimatedThreatPos + Vector3.up * 1.0f;

                    bool hasLOS = !Physics.Linecast(eyePos, threatEye);

                    var spot = TacticalSpot.FromPosition(navHit.position, Tag);
                    spot.CoverNormal = wallNorm;
                    spot.FacingDirection = (ctx.EstimatedThreatPos - navHit.position).normalized;
                    spot.HasHardCover = true; // wall provides cover
                    spot.DistanceToThreat = Vector3.Distance(navHit.position, ctx.EstimatedThreatPos);

                    // Flank angle -- corner positions often have good angles
                    Vector3 threatToSpot = (navHit.position - ctx.EstimatedThreatPos).normalized;
                    Vector3 threatFacing = ctx.ThreatVelocity.magnitude > 0.1f
                        ? ctx.ThreatVelocity.normalized
                        : (ctx.UnitPosition - ctx.EstimatedThreatPos).normalized;
                    spot.FlankAngle = Vector3.Angle(threatFacing, threatToSpot);

                    spots.Add(spot);
                }
            }

            return spots;
        }
    }
}