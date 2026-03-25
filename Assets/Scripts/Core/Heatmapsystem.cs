using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Squad heat map -- tracks where guards have been recently.
    /// Used by TacticalPatrolController to avoid revisiting areas.
    /// Extracted from HuntDirector.
    /// </summary>
    public static class HeatMapSystem
    {
        private const float GridSize = 2f;
        private const float DecayRate = 0.5f;

        private static readonly Dictionary<Vector2Int, float> _map
            = new Dictionary<Vector2Int, float>();

        public static void Register(Vector3 pos, float heat = 1f)
        {
            var cell = ToCell(pos);
            _map[cell] = Mathf.Clamp01(
                (_map.TryGetValue(cell, out float cur) ? cur : 0f) + heat);
        }

        public static void Clear(Vector3 pos) => _map.Remove(ToCell(pos));
        public static void ClearAll() => _map.Clear();

        public static float Get(Vector3 pos)
            => _map.TryGetValue(ToCell(pos), out float h) ? h : 0f;

        /// <summary>Call once per frame from HuntDirector.Update.</summary>
        public static void Decay(float dt)
        {
            if (_map.Count == 0) return;

            var keys = new List<Vector2Int>(_map.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                float v = _map[keys[i]] - DecayRate * dt;
                if (v <= 0f) _map.Remove(keys[i]);
                else _map[keys[i]] = v;
            }
        }

        private static Vector2Int ToCell(Vector3 pos)
            => new Vector2Int(
                Mathf.RoundToInt(pos.x / GridSize),
                Mathf.RoundToInt(pos.z / GridSize));
    }
}