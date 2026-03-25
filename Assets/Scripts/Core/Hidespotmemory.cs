using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Tracks positions where the player has been spotted.
    /// Used by search strategy to prioritise likely hiding spots.
    /// Extracted from HuntDirector.
    /// </summary>
    public static class HideSpotMemory
    {
        public struct HideSpotRecord
        {
            public Vector3 Position;
            public int FoundCount;
            public float LastFoundTime;
        }

        private const int MaxSpots = 10;
        private const float MergeRadius = 3f;

        private static readonly List<HideSpotRecord> _spots = new List<HideSpotRecord>();

        public static IReadOnlyList<HideSpotRecord> KnownSpots => _spots;

        public static void Add(Vector3 position)
        {
            for (int i = 0; i < _spots.Count; i++)
            {
                if (Vector3.Distance(_spots[i].Position, position) < MergeRadius)
                {
                    var r = _spots[i];
                    r.FoundCount++;
                    r.LastFoundTime = Time.time;
                    _spots[i] = r;
                    return;
                }
            }

            _spots.Add(new HideSpotRecord
            {
                Position = position,
                FoundCount = 1,
                LastFoundTime = Time.time,
            });

            if (_spots.Count > MaxSpots)
            {
                int oldest = 0;
                for (int i = 1; i < _spots.Count; i++)
                    if (_spots[i].LastFoundTime < _spots[oldest].LastFoundTime)
                        oldest = i;
                _spots.RemoveAt(oldest);
            }
        }

        public static List<HideSpotRecord> GetSnapshot() => new List<HideSpotRecord>(_spots);

        public static void Clear() => _spots.Clear();
    }
}