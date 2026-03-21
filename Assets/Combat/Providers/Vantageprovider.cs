using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Finds elevated positions that have line of sight to the estimated threat position.
    /// Favors positions above the threat for high ground advantage.
    /// </summary>
    public class VantageProvider : ITacticalProvider
    {
        public string Tag => "VantageProvider";
        public bool IsEnabled { get; set; } = true;

        [Range(2, 12)] public int SampleCount = 6;
        [Range(1f, 5f)] public float MinHeightGain = 1.5f;
        [Range(5f, 30f)] public float SearchRadius = 20f;

        public List<TacticalSpot> GetSpots(TacticalContext ctx)
        {
            var spots = new List<TacticalSpot>();

            // Sample positions in a ring around the unit, looking for elevated NavMesh
            for (int i = 0; i < SampleCount; i++)
            {
                float angle = i * (360f / SampleCount);
                float radius = SearchRadius * 0.6f;
                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                Vector3 candidate = ctx.UnitPosition + dir * radius;

                // Sample slightly above to catch ramps, stairs, crates
                for (float heightOffset = MinHeightGain; heightOffset <= 4f; heightOffset += 0.5f)
                {
                    Vector3 elevated = candidate + Vector3.up * heightOffset;

                    if (!NavMesh.SamplePosition(elevated, out NavMeshHit hit,
                        1.5f, ctx.NavMeshMask)) continue;

                    float heightGain = hit.position.y - ctx.UnitPosition.y;
                    if (heightGain < MinHeightGain) continue;

                    // Must have LOS to estimated threat position
                    Vector3 eyePos = hit.position + Vector3.up * 1.6f;
                    Vector3 targetEye = ctx.EstimatedThreatPos + Vector3.up * 1.0f;
                    if (Physics.Linecast(eyePos, targetEye)) continue;

                    float dist = Vector3.Distance(ctx.UnitPosition, hit.position);
                    if (dist > ctx.SearchRadius) continue;

                    var spot = TacticalSpot.FromPosition(hit.position, Tag);
                    spot.Height = heightGain;
                    spot.DistanceToThreat = Vector3.Distance(hit.position, ctx.EstimatedThreatPos);
                    spot.FacingDirection = (ctx.EstimatedThreatPos - hit.position).normalized;
                    spot.HasHardCover = false;

                    spots.Add(spot);
                    break; // found valid height at this angle -- move to next
                }
            }

            return spots;
        }
    }
}