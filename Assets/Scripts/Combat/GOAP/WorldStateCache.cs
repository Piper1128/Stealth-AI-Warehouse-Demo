using UnityEngine;
using StealthHuntAI.Combat.CQB;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Per-unit component that caches expensive WorldState checks.
    /// Added automatically by WorldState.Build -- no manual setup required.
    /// Replaces the static dictionary cache which was not cleared on scene reload.
    /// </summary>
    [AddComponentMenu("")] // hide from Add Component menu
    public class WorldStateCache : MonoBehaviour
    {
        private struct Cache
        {
            public bool HighGround;
            public bool NearEntry;
            public bool Chokepoint;
            public float Time;
        }

        private Cache _cache;
        private const float Interval = 0.5f;

        public WorldState.CachedChecks Get(StealthHuntAI unit)
        {
            if (Time.time - _cache.Time < Interval)
                return new WorldState.CachedChecks
                {
                    HighGround = _cache.HighGround,
                    NearEntry = _cache.NearEntry,
                    Chokepoint = _cache.Chokepoint,
                    Time = _cache.Time,
                };

            _cache = new Cache
            {
                HighGround = CheckHighGround(unit),
                NearEntry = CheckNearEntry(unit),
                Chokepoint = false,
                Time = Time.time,
            };

            return new WorldState.CachedChecks
            {
                HighGround = _cache.HighGround,
                NearEntry = _cache.NearEntry,
                Chokepoint = _cache.Chokepoint,
                Time = _cache.Time,
            };
        }

        private static bool CheckHighGround(StealthHuntAI unit)
        {
            Vector3 pos = unit.transform.position;
            float[] heights = { 2f, 3f, 4f, 5f };
            float[] angles = { 0f, 90f, 180f, 270f };

            foreach (float h in heights)
                foreach (float a in angles)
                {
                    Vector3 dir = Quaternion.Euler(0, a, 0) * Vector3.forward;
                    Vector3 candidate = pos + dir * 8f + Vector3.up * h;

                    if (!UnityEngine.AI.NavMesh.SamplePosition(candidate,
                        out var hit, 1.5f, UnityEngine.AI.NavMesh.AllAreas)) continue;
                    if (hit.position.y - pos.y < 1.5f) continue;

                    var path = new UnityEngine.AI.NavMeshPath();
                    if (!UnityEngine.AI.NavMesh.CalculatePath(pos, hit.position,
                        UnityEngine.AI.NavMesh.AllAreas, path)) continue;
                    if (path.status == UnityEngine.AI.NavMeshPathStatus.PathComplete)
                        return true;
                }
            return false;
        }

        private static bool CheckNearEntry(StealthHuntAI unit)
        {
            var brain = TacticalBrain.GetOrCreate(unit.squadID);
            if (brain.CQB.IsActive) return true;
            var ep = EntryPointRegistry.FindNearest(unit.transform.position, unit);
            return ep != null && ep.DistToStack(unit.transform.position) < 12f;
        }
    }
}