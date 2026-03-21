using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Finds cover points near the unit that offer protection from the threat.
    /// Uses registered CoverPoints from HuntDirector + auto-scanned positions.
    /// </summary>
    public class CoverProvider : ITacticalProvider
    {
        public string Tag => "CoverProvider";
        public bool IsEnabled { get; set; } = true;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();
            var allCover = HuntDirector.AllCoverPoints;

            for (int i = 0; i < allCover.Count; i++)
            {
                var cp = allCover[i] as CoverPoint;
                if (cp == null) continue;

                // Skip occupied cover (reserved by another unit)
                if (cp.IsOccupied && cp.Occupant != ctx.Unit) continue;

                float dist = Vector3.Distance(ctx.UnitPosition, cp.transform.position);
                if (dist > ctx.SearchRadius) continue;

                // Must be reachable via NavMesh
                if (!NavMesh.SamplePosition(cp.transform.position,
                    out NavMeshHit hit, 1f, ctx.NavMeshMask)) continue;

                var spot = TacticalSpot.FromCoverPoint(cp, Tag);
                spot.DistanceToThreat = Vector3.Distance(
                    cp.transform.position, ctx.EstimatedThreatPos);
                spot.Height = cp.transform.position.y - ctx.UnitPosition.y;

                // Check if this cover actually protects from threat direction
                Vector3 toThreat = (ctx.EstimatedThreatPos - cp.transform.position).normalized;
                spot.FlankAngle = Vector3.Angle(cp.transform.forward, toThreat);

                spots.Add(spot);
            }

            return spots;
        }
    }
}