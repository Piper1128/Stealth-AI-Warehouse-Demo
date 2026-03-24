using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Builds safe waypoint chains for tactical movement.
    /// Used by FlankAction, AdvanceAggressivelyAction and WithdrawAction
    /// so guards never run across exposed ground.
    ///
    /// A waypoint is "safe" if:
    ///   1. It is reachable via complete NavMesh path
    ///   2. It is not in direct LOS of the threat position
    ///   3. It is not too close to another squad member
    /// </summary>
    public static class TacticalPathfinder
    {
        // ---------- Public API -----------------------------------------------

        /// <summary>
        /// Build a flank waypoint chain around threat.
        /// Returns null if no safe route found.
        /// </summary>
        public static List<Vector3> BuildFlankRoute(
            StealthHuntAI unit,
            Vector3 threatPos,
            float flankRadius = 18f)
        {
            Vector3 unitPos = unit.transform.position;
            Vector3 toThreat = (threatPos - unitPos);
            toThreat.y = 0f;

            // Try 4 flank angles -- pick the one with the safest route
            float[] angles = { 80f, -80f, 110f, -110f };
            foreach (float angle in angles)
            {
                Vector3 flankDir = Quaternion.Euler(0, angle, 0) * toThreat.normalized;
                Vector3 flankDest = threatPos + flankDir * (flankRadius * 0.5f);

                // Sample onto NavMesh
                if (!NavMesh.SamplePosition(flankDest, out var hit, 4f, NavMesh.AllAreas))
                    continue;
                flankDest = hit.position;

                // Build waypoints along the route
                var chain = BuildWaypointChain(unitPos, flankDest, threatPos,
                    unit.squadID, unit, WaypointMode.Flank);

                if (chain != null && chain.Count > 0)
                    return chain;
            }

            return null;
        }

        /// <summary>
        /// Build a cover-to-cover advance route toward threat.
        /// Returns intermediate safe waypoints, not the threat position itself.
        /// </summary>
        public static List<Vector3> BuildAdvanceRoute(
            StealthHuntAI unit,
            Vector3 threatPos,
            float stopDistance = 8f)
        {
            Vector3 unitPos = unit.transform.position;
            Vector3 dir = (threatPos - unitPos).normalized;

            // Target a position short of the threat
            Vector3 dest = threatPos - dir * stopDistance;
            if (!NavMesh.SamplePosition(dest, out var hit, 3f, NavMesh.AllAreas))
                dest = threatPos;
            else
                dest = hit.position;

            return BuildWaypointChain(unitPos, dest, threatPos,
                unit.squadID, unit, WaypointMode.Advance);
        }

        /// <summary>
        /// Build a safe withdraw route away from threat.
        /// </summary>
        public static List<Vector3> BuildWithdrawRoute(
            StealthHuntAI unit,
            Vector3 threatPos,
            float withdrawDist = 20f)
        {
            Vector3 unitPos = unit.transform.position;
            Vector3 awayDir = (unitPos - threatPos).normalized;

            // Try several withdraw angles
            float[] angles = { 0f, 30f, -30f, 60f, -60f };
            foreach (float angle in angles)
            {
                Vector3 tryDir = Quaternion.Euler(0, angle, 0) * awayDir;
                Vector3 tryDest = unitPos + tryDir * withdrawDist;

                if (!NavMesh.SamplePosition(tryDest, out var hit, 4f, NavMesh.AllAreas))
                    continue;

                var chain = BuildWaypointChain(unitPos, hit.position, threatPos,
                    unit.squadID, unit, WaypointMode.Withdraw);

                if (chain != null && chain.Count > 0)
                    return chain;
            }

            return null;
        }

        // ---------- Core waypoint builder ------------------------------------

        private enum WaypointMode { Flank, Advance, Withdraw }

        private static List<Vector3> BuildWaypointChain(
            Vector3 from,
            Vector3 to,
            Vector3 threatPos,
            int squadID,
            StealthHuntAI unit,
            WaypointMode mode)
        {
            // Verify full path exists
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path))
                return null;
            if (path.status != NavMeshPathStatus.PathComplete)
                return null;

            var corners = path.corners;
            if (corners.Length < 2) return null;

            float totalDist = Vector3.Distance(from, to);
            var waypoints = new List<Vector3>();
            var squadUnits = HuntDirector.AllUnits;

            // --- Cover-to-cover routing ---
            // Sample positions along the route and find covered ones
            // This gives us a cover-hugging path rather than a straight line
            {
                var candidates = new List<(float t, Vector3 pos)>();
                int steps = Mathf.Clamp(Mathf.RoundToInt(totalDist / 4f), 2, 8);

                for (int s = 1; s < steps; s++)
                {
                    float pct = (float)s / steps;
                    Vector3 sample = Vector3.Lerp(from, to, pct);

                    // Sample 8 directions at this point -- find one with cover
                    float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };
                    float[] radii = { 2f, 4f, 6f };

                    foreach (float r in radii)
                        foreach (float a in angles)
                        {
                            Vector3 candidate = sample
                                + Quaternion.Euler(0, a, 0) * Vector3.forward * r;

                            if (!NavMesh.SamplePosition(candidate, out var hit, 1f,
                                NavMesh.AllAreas)) continue;

                            Vector3 cp = hit.position;

                            // Must have cover from threat
                            if (IsExposedToThreat(cp, threatPos)) continue;

                            // Must not stray too far from route
                            if (DistanceToSegment(cp, from, to) > 8f) continue;

                            float t2 = Vector3.Dot(cp - from, (to - from).normalized)
                                     / Mathf.Max(totalDist, 0.1f);
                            if (t2 < 0.05f || t2 > 0.95f) continue;

                            candidates.Add((t2, cp));
                            goto nextStep; // one good point per step is enough
                        }
                    nextStep:;
                }

                candidates.Sort((a, b) => a.t.CompareTo(b.t));

                // Deduplicate -- skip points too close together
                Vector3 lastAdded = from;
                foreach (var (_, pos) in candidates)
                {
                    if (Vector3.Distance(pos, lastAdded) < 2.5f) continue;

                    bool tooClose = false;
                    for (int j = 0; j < squadUnits.Count; j++)
                    {
                        var u = squadUnits[j];
                        if (u == null || u == unit || u.squadID != squadID) continue;
                        if (Vector3.Distance(pos, u.transform.position) < 1.5f)
                        { tooClose = true; break; }
                    }
                    if (tooClose) continue;

                    waypoints.Add(pos);
                    lastAdded = pos;
                }
            }

            // --- Fallback to NavMesh corners if no cover found ---
            if (waypoints.Count == 0)
            {
                for (int i = 1; i < corners.Length; i++)
                {
                    Vector3 wp = corners[i];

                    if (mode != WaypointMode.Advance && IsExposedToThreat(wp, threatPos))
                    {
                        Vector3? safe = FindSafeAlternative(wp, threatPos);
                        if (safe == null) continue;
                        wp = safe.Value;
                    }

                    bool tooClose = false;
                    for (int j = 0; j < squadUnits.Count; j++)
                    {
                        var u = squadUnits[j];
                        if (u == null || u == unit || u.squadID != squadID) continue;
                        if (Vector3.Distance(wp, u.transform.position) < 1.5f)
                        { tooClose = true; break; }
                    }
                    if (tooClose) continue;

                    var testPath = new NavMeshPath();
                    if (!NavMesh.CalculatePath(unit.transform.position, wp,
                        NavMesh.AllAreas, testPath)
                     || testPath.status != NavMeshPathStatus.PathComplete)
                        continue;

                    waypoints.Add(wp);
                }
            }

            // Always add final destination
            waypoints.Add(to);

            return waypoints.Count > 0 ? waypoints : null;
        }

        // ---------- Geometry helpers -----------------------------------------

        private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude);
            return Vector3.Distance(point, a + t * ab);
        }


        // ---------- Safety checks --------------------------------------------

        private static bool IsExposedToThreat(Vector3 point, Vector3 threatPos)
        {
            Vector3 dir = (threatPos - point);
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;

            // Raycast from point toward threat -- if clear, point is exposed
            int layerMask = ~LayerMask.GetMask("Ignore Raycast");
            return !Physics.Raycast(point + Vector3.up * 1f,
                dir.normalized, dist - 0.5f, layerMask);
        }

        private static Vector3? FindSafeAlternative(Vector3 exposed, Vector3 threatPos)
        {
            // Sample 8 nearby positions -- pick closest that is not exposed
            float[] radii = { 1.5f, 2.5f, 3.5f };
            float[] angles = { 0, 45, 90, 135, 180, 225, 270, 315 };

            float bestDist = float.MaxValue;
            Vector3? best = null;

            foreach (float r in radii)
                foreach (float a in angles)
                {
                    Vector3 candidate = exposed
                        + Quaternion.Euler(0, a, 0) * Vector3.forward * r;

                    if (!NavMesh.SamplePosition(candidate, out var hit, 1f, NavMesh.AllAreas))
                        continue;

                    if (IsExposedToThreat(hit.position, threatPos)) continue;

                    float d = Vector3.Distance(hit.position, exposed);
                    if (d < bestDist) { bestDist = d; best = hit.position; }
                }

            return best;
        }

        // ---------- Waypoint follower helper ---------------------------------

        /// <summary>
        /// Move unit along waypoint chain.
        /// Returns true when final waypoint is reached.
        /// Call every frame from Execute().
        /// </summary>
        public static bool FollowWaypoints(StealthHuntAI unit,
                                            List<Vector3> waypoints,
                                            ref int waypointIndex,
                                            float arrivalDist = 1.2f)
        {
            if (waypoints == null || waypointIndex >= waypoints.Count)
                return true;

            Vector3 target = waypoints[waypointIndex];
            unit.CombatMoveTo(target);

            float dist = Vector3.Distance(unit.transform.position, target);
            if (dist < arrivalDist)
            {
                waypointIndex++;
                if (waypointIndex >= waypoints.Count)
                    return true;
            }

            return false;
        }
    }
}