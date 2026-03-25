using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Tactical scoring layer on top of NavRouter.
    /// Filters and scores waypoints based on cover, LOS and squad separation.
    /// TacticalPathfinder now delegates here for cover-to-cover routing.
    /// </summary>
    public static class TacticalFilter
    {
        // ---------- LOS checks -----------------------------------------------

        public static bool IsExposedToThreat(Vector3 point, Vector3 threatPos)
        {
            Vector3 dir = threatPos - point;
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;
            int mask = ~LayerMask.GetMask("Ignore Raycast");
            return !Physics.Raycast(point + Vector3.up, dir.normalized, dist - 0.5f, mask);
        }

        public static bool HasLOS(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float dist = dir.magnitude;
            if (dist < 0.1f) return true;
            int mask = ~LayerMask.GetMask("Ignore Raycast");
            return !Physics.Raycast(from + Vector3.up * 1.5f, dir.normalized,
                dist - 0.3f, mask);
        }

        // ---------- Cover-filtered path building -----------------------------

        /// <summary>
        /// Build a waypoint chain from->to that hugs cover and avoids threat LOS.
        /// Falls back to raw NavMesh corners if no covered route found.
        /// </summary>
        public static List<Vector3> BuildCoveredRoute(
            Vector3 from, Vector3 to, Vector3 threatPos,
            int squadID, StealthHuntAI unit, bool allowExposed = false)
        {
            var rawPath = NavRouter.BuildPath(from, to);
            if (rawPath == null) return null;

            float totalDist = Vector3.Distance(from, to);
            var waypoints = new List<Vector3>();
            var squad = HuntDirector.AllUnits;

            int steps = Mathf.Clamp(Mathf.RoundToInt(totalDist / 4f), 2, 8);
            float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };
            float[] radii = { 2f, 4f, 6f };

            Vector3 lastAdded = from;

            for (int s = 1; s < steps; s++)
            {
                float pct = (float)s / steps;
                Vector3 sample = Vector3.Lerp(from, to, pct);
                bool found = false;

                foreach (float r in radii)
                {
                    if (found) break;
                    foreach (float a in angles)
                    {
                        Vector3 candidate = sample +
                            Quaternion.Euler(0, a, 0) * Vector3.forward * r;

                        if (!NavMesh.SamplePosition(candidate, out var hit,
                            1f, NavMesh.AllAreas)) continue;

                        Vector3 cp = hit.position;

                        if (!allowExposed && IsExposedToThreat(cp, threatPos)) continue;

                        float seg = DistToSegment(cp, from, to);
                        if (seg > 8f) continue;

                        float t = Vector3.Dot(cp - from, (to - from).normalized)
                                / Mathf.Max(totalDist, 0.1f);
                        if (t < 0.05f || t > 0.95f) continue;

                        if (Vector3.Distance(cp, lastAdded) < 2.5f) continue;

                        if (TooCloseToSquad(cp, unit, squadID, squad)) continue;

                        waypoints.Add(cp);
                        lastAdded = cp;
                        found = true;
                        break;
                    }
                }
            }

            // Fallback to raw corners
            if (waypoints.Count == 0)
            {
                foreach (var wp in rawPath)
                {
                    if (!allowExposed && IsExposedToThreat(wp, threatPos)) continue;
                    if (TooCloseToSquad(wp, unit, squadID, squad)) continue;
                    waypoints.Add(wp);
                }
            }

            waypoints.Add(to);
            return waypoints.Count > 0 ? waypoints : null;
        }

        // ---------- Cover spot finding ---------------------------------------

        /// <summary>
        /// Find a safe alternative position near an exposed point.
        /// </summary>
        public static Vector3? FindCoverNear(Vector3 exposed, Vector3 threatPos)
        {
            float[] radii = { 1.5f, 2.5f, 3.5f };
            float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };

            float bestDist = float.MaxValue;
            Vector3? best = null;

            foreach (float r in radii)
                foreach (float a in angles)
                {
                    Vector3 c = exposed + Quaternion.Euler(0, a, 0) * Vector3.forward * r;
                    if (!NavMesh.SamplePosition(c, out var hit, 1f, NavMesh.AllAreas))
                        continue;
                    if (IsExposedToThreat(hit.position, threatPos)) continue;
                    float d = Vector3.Distance(hit.position, exposed);
                    if (d < bestDist) { bestDist = d; best = hit.position; }
                }

            return best;
        }

        // ---------- Helpers --------------------------------------------------

        private static float DistToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(point, a + t * ab);
        }

        private static bool TooCloseToSquad(Vector3 pos, StealthHuntAI unit,
                                             int squadID,
                                             IReadOnlyList<StealthHuntAI> squad)
        {
            for (int i = 0; i < squad.Count; i++)
            {
                var u = squad[i];
                if (u == null || u == unit || u.squadID != squadID) continue;
                if (Vector3.Distance(pos, u.transform.position) < 1.5f) return true;
            }
            return false;
        }
    }
}