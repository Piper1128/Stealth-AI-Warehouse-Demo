using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat.CQB
{
    /// <summary>
    /// Static registry of all EntryPoints in the scene.
    /// HuntDirector and CQBController use this to find
    /// the best entry point for a given threat position.
    /// </summary>
    public static class EntryPointRegistry
    {
        private static readonly List<EntryPoint> _entries = new List<EntryPoint>();

        // ---------- Spatial hash grid ----------------------------------------
        // Divides scene into cells -- only checks entry points in nearby cells
        // Build once at scene load, invalidate when entry points change

        private const float CellSize = 8f;
        private static readonly Dictionary<long, List<EntryPoint>> _grid
            = new Dictionary<long, List<EntryPoint>>();
        private static bool _gridDirty = true;

        private static long CellKey(int x, int z) => ((long)x << 32) | (uint)z;

        private static void RebuildGrid()
        {
            _grid.Clear();
            for (int i = 0; i < _entries.Count; i++)
            {
                var ep = _entries[i];
                int cx = Mathf.FloorToInt(ep.transform.position.x / CellSize);
                int cz = Mathf.FloorToInt(ep.transform.position.z / CellSize);
                long key = CellKey(cx, cz);
                if (!_grid.TryGetValue(key, out var list))
                    _grid[key] = list = new List<EntryPoint>();
                list.Add(ep);
            }
            _gridDirty = false;
        }

        private static List<EntryPoint> GetCellCandidates(Vector3 pos)
        {
            if (_gridDirty) RebuildGrid();

            int cx = Mathf.FloorToInt(pos.x / CellSize);
            int cz = Mathf.FloorToInt(pos.z / CellSize);

            var result = new List<EntryPoint>();
            // Check 3x3 neighbourhood -- covers up to CellSize * 1.5 away
            for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long key = CellKey(cx + dx, cz + dz);
                    if (_grid.TryGetValue(key, out var list))
                        result.AddRange(list);
                }

            // Fallback -- if no neighbours found use all entries
            return result.Count > 0 ? result : _entries;
        }

        // ---------- Per-unit nearest cache -----------------------------------
        // Avoids re-querying when unit hasnt moved significantly

        private static readonly Dictionary<StealthHuntAI, CachedQuery> _cache
            = new Dictionary<StealthHuntAI, CachedQuery>();

        private struct CachedQuery
        {
            public Vector3 LastPos;
            public EntryPoint Result;
        }

        private const float CacheInvalidateDist = 2f;

        private static bool TryGetCached(StealthHuntAI unit, Vector3 pos,
                                          out EntryPoint result)
        {
            if (_cache.TryGetValue(unit, out var cached)
             && Vector3.Distance(pos, cached.LastPos) < CacheInvalidateDist)
            {
                result = cached.Result;
                return true;
            }
            result = null;
            return false;
        }

        private static void SetCache(StealthHuntAI unit, Vector3 pos, EntryPoint ep)
            => _cache[unit] = new CachedQuery { LastPos = pos, Result = ep };

        // ---------- Registration ---------------------------------------------

        public static void Register(EntryPoint ep)
        {
            if (!_entries.Contains(ep))
            {
                _entries.Add(ep);
                _gridDirty = true;
            }
        }

        public static void Unregister(EntryPoint ep)
        {
            _entries.Remove(ep);
            _gridDirty = true;
        }

        public static IReadOnlyList<EntryPoint> All => _entries;

        // ---------- Queries --------------------------------------------------

        /// <summary>
        /// Find the best entry point for breaching toward a threat position.
        /// Prefers entry points that:
        ///   1. Are not already occupied
        ///   2. Face toward the threat (guard approaches from behind)
        ///   3. Are within reach of the unit
        /// </summary>
        public static EntryPoint FindBest(Vector3 unitPos, Vector3 threatPos,
                                           float maxDist = 12f)
        {
            EntryPoint best = null;
            float bestScore = float.MinValue;

            var candidates = GetCellCandidates(unitPos);
            for (int i = 0; i < candidates.Count; i++)
            {
                var ep = candidates[i];
                if (!ep.isBreachable) continue;
                if (ep.IsOccupied) continue;

                float distUnit = ep.DistToStack(unitPos);
                float distThreat = Vector3.Distance(ep.transform.position, threatPos);

                if (distUnit > maxDist) continue;

                // Score: close to unit, threat on the other side of door
                float score = -distUnit * 0.6f - distThreat * 0.4f;

                // Bonus if entry faces toward threat
                Vector3 toThreat = (threatPos - ep.transform.position).normalized;
                float dot = Vector3.Dot(ep.transform.forward, toThreat);
                score += dot * 3f;

                if (score > bestScore) { bestScore = score; best = ep; }
            }

            return best;
        }

        /// <summary>
        /// Find all entry points leading to a specific room.
        /// </summary>
        public static List<EntryPoint> FindByRoom(string roomID)
        {
            var result = new List<EntryPoint>();
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].leadsToRoomID == roomID)
                    result.Add(_entries[i]);
            return result;
        }

        /// <summary>
        /// Find nearest entry point to a position.
        /// Uses spatial grid + per-unit cache -- nearly O(1) in most cases.
        /// </summary>
        public static EntryPoint FindNearest(Vector3 pos,
                                              StealthHuntAI unit = null)
        {
            // Cache hit -- unit hasnt moved enough to requery
            if (unit != null && TryGetCached(unit, pos, out var cached))
                return cached;

            // Grid lookup -- only nearby cells
            var candidates = GetCellCandidates(pos);

            EntryPoint best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (!candidates[i].isBreachable) continue;
                float d = candidates[i].DistToStack(pos);
                if (d < bestDist) { bestDist = d; best = candidates[i]; }
            }

            if (unit != null) SetCache(unit, pos, best);
            return best;
        }
    }
}