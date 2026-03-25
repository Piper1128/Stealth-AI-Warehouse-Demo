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
            Vector3 from, Vector3 to, Vector3 threatPos,
            int squadID, StealthHuntAI unit, WaypointMode mode)
        {
            // Delegate to TacticalFilter -- pure tactical scoring on top of NavRouter
            bool allowExposed = mode == WaypointMode.Advance;
            return TacticalFilter.BuildCoveredRoute(
                from, to, threatPos, squadID, unit, allowExposed);
        }

        // ---------- Geometry helpers -----------------------------------------


        // ---------- Safety checks -- delegated to TacticalFilter ---------------

        private static bool IsExposedToThreat(Vector3 point, Vector3 threatPos)
            => TacticalFilter.IsExposedToThreat(point, threatPos);

        private static Vector3? FindSafeAlternative(Vector3 exposed, Vector3 threatPos)
            => TacticalFilter.FindCoverNear(exposed, threatPos);

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