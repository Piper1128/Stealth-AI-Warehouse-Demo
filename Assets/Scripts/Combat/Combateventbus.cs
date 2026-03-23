using System;
using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Combat event types that can interrupt current GOAP plan.
    /// </summary>
    public enum CombatEventType
    {
        DamageTaken,      // unit took damage -- seek cover
        BuddyDown,        // buddy was killed -- hold and suppress
        Ambushed,         // taking damage while advancing -- sprint and reassess
        ThreatFlank,      // threat appeared from unexpected angle -- reorient
        ThreatLost,       // lost LOS -- yield to core search
        ThreatFound,      // regained LOS -- engage immediately
        BuddyArrived,     // buddy reached cover -- role swap
        BuddyNeedsHelp,   // buddy taking heavy fire -- suppress to help
    }

    /// <summary>
    /// Combat event with context data.
    /// </summary>
    public struct CombatEvent
    {
        public CombatEventType Type;
        public StealthHuntAI Source;
        public Vector3 Position;
        public Vector3 Direction;
        public float Severity;   // 0-1
    }

    /// <summary>
    /// Per-unit event bus for combat interrupts.
    /// Guards subscribe to events and react immediately without waiting for replan.
    ///
    /// F.E.A.R. style: important events are raised by game logic (damage, death)
    /// and interrupt the current action immediately.
    /// </summary>
    public class CombatEventBus
    {
        private readonly List<CombatEvent> _pending = new List<CombatEvent>();

        // ---------- Publishing -----------------------------------------------

        public void Raise(CombatEventType type, StealthHuntAI source,
                          Vector3 position = default,
                          Vector3 direction = default,
                          float severity = 1f)
        {
            _pending.Add(new CombatEvent
            {
                Type = type,
                Source = source,
                Position = position,
                Direction = direction,
                Severity = severity,
            });
        }

        // ---------- Consuming ------------------------------------------------

        /// <summary>
        /// Drain all pending events and return the highest priority one.
        /// Returns null if no events pending.
        /// </summary>
        public CombatEvent? ConsumeHighestPriority()
        {
            if (_pending.Count == 0) return null;

            CombatEvent best = _pending[0];
            int bestPriority = GetPriority(best.Type);

            for (int i = 1; i < _pending.Count; i++)
            {
                int p = GetPriority(_pending[i].Type);
                if (p > bestPriority) { best = _pending[i]; bestPriority = p; }
            }

            _pending.Clear();
            return best;
        }

        public bool HasEvents => _pending.Count > 0;

        private static int GetPriority(CombatEventType type) => type switch
        {
            CombatEventType.Ambushed => 10,
            CombatEventType.BuddyDown => 9,
            CombatEventType.DamageTaken => 8,
            CombatEventType.ThreatFlank => 7,
            CombatEventType.BuddyNeedsHelp => 6,
            CombatEventType.ThreatFound => 5,
            CombatEventType.BuddyArrived => 4,
            CombatEventType.ThreatLost => 3,
            _ => 0,
        };

        // ---------- Static per-unit registry ---------------------------------

        private static readonly Dictionary<StealthHuntAI, CombatEventBus> _buses
            = new Dictionary<StealthHuntAI, CombatEventBus>();

        public static CombatEventBus Get(StealthHuntAI unit)
        {
            if (!_buses.TryGetValue(unit, out var bus))
            {
                bus = new CombatEventBus();
                _buses[unit] = bus;
            }
            return bus;
        }

        public static void RaiseSquad(int squadID, CombatEventType type,
                                       StealthHuntAI source,
                                       Vector3 position = default,
                                       float severity = 1f)
        {
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.squadID != squadID) continue;
                Get(u).Raise(type, source, position, default, severity);
            }
        }

        public static void Clear(StealthHuntAI unit)
            => _buses.Remove(unit);
    }
}