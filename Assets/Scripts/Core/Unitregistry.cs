using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Central registry of all StealthHuntAI units and the player target.
    /// Replaces the static unit/target management in HuntDirector.
    /// </summary>
    public static class UnitRegistry
    {
        private static StealthTarget _target;
        private static readonly List<StealthHuntAI> _units = new List<StealthHuntAI>();

        public static IReadOnlyList<StealthHuntAI> AllUnits => _units;
        public static StealthTarget Target => _target;

        public static void RegisterTarget(StealthTarget target)
        {
            _target = target;
        }

        public static void UnregisterTarget(StealthTarget target)
        {
            if (_target == target) _target = null;
        }

        public static void RegisterUnit(StealthHuntAI unit)
        {
            if (!_units.Contains(unit)) _units.Add(unit);
        }

        public static void UnregisterUnit(StealthHuntAI unit)
        {
            _units.Remove(unit);
        }

        public static void Clear()
        {
            _units.Clear();
            _target = null;
        }
    }
}