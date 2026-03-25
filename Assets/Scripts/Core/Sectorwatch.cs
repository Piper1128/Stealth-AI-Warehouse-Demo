using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Tracks which compass sectors are being watched by guards.
    /// Prevents multiple guards from watching the same direction.
    /// Extracted from HuntDirector.
    /// </summary>
    public static class SectorWatch
    {
        private static readonly Dictionary<int, StealthHuntAI> _watchers
            = new Dictionary<int, StealthHuntAI>();

        public static void Register(Vector3 direction, StealthHuntAI unit)
            => _watchers[ToSector(direction)] = unit;

        public static void Unregister(StealthHuntAI unit)
        {
            var keys = new List<int>();
            foreach (var kv in _watchers)
                if (kv.Value == unit) keys.Add(kv.Key);
            foreach (var k in keys) _watchers.Remove(k);
        }

        public static bool IsWatched(Vector3 direction)
        {
            if (!_watchers.TryGetValue(ToSector(direction), out var w)) return false;
            return w != null;
        }

        public static void Clear() => _watchers.Clear();

        private static int ToSector(Vector3 dir)
        {
            dir.y = 0f;
            float deg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (deg < 0) deg += 360f;
            return Mathf.RoundToInt(deg / 45f) % 8;
        }
    }
}