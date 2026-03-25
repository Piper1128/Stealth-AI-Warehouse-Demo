using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Pure NavMesh pathfinding utilities.
    /// No tactical scoring -- just path building and following.
    /// TacticalFilter sits on top of this to score and select waypoints.
    /// </summary>
    public static class NavRouter
    {
        /// <summary>
        /// Build a complete NavMesh path from start to destination.
        /// Returns null if no complete path exists.
        /// </summary>
        public static List<Vector3> BuildPath(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path))
                return null;
            if (path.status != NavMeshPathStatus.PathComplete)
                return null;

            var result = new List<Vector3>(path.corners.Length);
            for (int i = 1; i < path.corners.Length; i++)
                result.Add(path.corners[i]);
            return result;
        }

        /// <summary>
        /// Sample a valid NavMesh position near the given world position.
        /// Returns false if no NavMesh found within radius.
        /// </summary>
        public static bool Sample(Vector3 pos, float radius, out Vector3 result)
        {
            if (NavMesh.SamplePosition(pos, out var hit, radius, NavMesh.AllAreas))
            {
                result = hit.position;
                return true;
            }
            result = pos;
            return false;
        }

        /// <summary>
        /// Follow a waypoint chain. Call every frame.
        /// Returns true when the final waypoint is reached.
        /// Moves agent via CombatMoveTo -- call speed before this if needed.
        /// </summary>
        public static bool Follow(StealthHuntAI unit, List<Vector3> waypoints,
                                   ref int index, float arrivalDist = 1.2f)
        {
            if (waypoints == null || index >= waypoints.Count) return true;

            Vector3 target = waypoints[index];
            unit.CombatMoveTo(target);

            if (Vector3.Distance(unit.transform.position, target) < arrivalDist)
            {
                index++;
                if (index >= waypoints.Count) return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a complete path exists between two points.
        /// </summary>
        public static bool HasPath(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path)) return false;
            return path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>
        /// Returns NavMesh path length between two points. Returns -1 if no path.
        /// </summary>
        public static float PathLength(Vector3 from, Vector3 to)
        {
            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, path)) return -1f;
            if (path.status != NavMeshPathStatus.PathComplete) return -1f;

            float length = 0f;
            for (int i = 1; i < path.corners.Length; i++)
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            return length;
        }
    }
}