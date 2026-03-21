using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Tactical buddy pair system.
    /// Divides a squad into pairs -- each pair has a Tracker and a Suppressor.
    /// Coordinates bounding overwatch via role swap signals.
    ///
    /// Integrates with:
    ///   TacticalBrain  -- reads squad members, shares threat intel
    ///   WorldState     -- writes BuddyRole so GOAP planner can read it
    ///   GoapActions    -- Suppress and Advance read buddy signals
    ///   FormationController -- Suppressor holds geometric offset from Tracker
    /// </summary>
    public class BuddySystem
    {
        // ---------- Pair -----------------------------------------------------

        public class BuddyPair
        {
            public StealthHuntAI Tracker;
            public StealthHuntAI Suppressor;
            public BuddyRole TrackerRole = BuddyRole.Tracker;
            public BuddyRole SuppressorRole = BuddyRole.Suppressor;

            // Signals
            public bool TrackerArrived { get; private set; }
            public bool SuppressorReady { get; private set; }
            public float SwapCooldown { get; private set; }

            private const float MinSwapInterval = 2.5f;

            public void SignalArrived(StealthHuntAI unit)
            {
                if (SwapCooldown > 0f) return;
                if (unit == Tracker) TrackerArrived = true;
                if (unit == Suppressor) SuppressorReady = true;
            }

            public void Update(float dt)
            {
                SwapCooldown = Mathf.Max(0f, SwapCooldown - dt);

                // Swap roles when Tracker signals arrived
                if (TrackerArrived && SwapCooldown <= 0f)
                    SwapRoles();
            }

            private void SwapRoles()
            {
                var temp = Tracker;
                Tracker = Suppressor;
                Suppressor = temp;
                TrackerArrived = false;
                SuppressorReady = false;
                SwapCooldown = MinSwapInterval;
            }

            public BuddyRole GetRole(StealthHuntAI unit)
            {
                if (unit == Tracker) return BuddyRole.Tracker;
                if (unit == Suppressor) return BuddyRole.Suppressor;
                return BuddyRole.None;
            }

            public StealthHuntAI GetBuddy(StealthHuntAI unit)
                => unit == Tracker ? Suppressor : Tracker;

            public bool IsValid => Tracker != null && Suppressor != null;
        }

        // ---------- Registry -------------------------------------------------

        private readonly List<BuddyPair> _pairs = new List<BuddyPair>();
        private readonly List<StealthHuntAI> _singles = new List<StealthHuntAI>();

        // ---------- Static registry ------------------------------------------

        private static readonly Dictionary<int, BuddySystem> _systems
            = new Dictionary<int, BuddySystem>();

        public static BuddySystem GetOrCreate(int squadID)
        {
            if (!_systems.TryGetValue(squadID, out var sys))
            {
                sys = new BuddySystem();
                _systems[squadID] = sys;
            }
            return sys;
        }

        public static void Clear() => _systems.Clear();

        // ---------- Pair building --------------------------------------------

        /// <summary>
        /// Rebuild pairs from current squad members.
        /// Call when a unit joins or dies.
        /// </summary>
        public void RebuildPairs(List<StealthHuntAI> members)
        {
            _pairs.Clear();
            _singles.Clear();

            for (int i = 0; i + 1 < members.Count; i += 2)
            {
                var pair = new BuddyPair
                {
                    Tracker = members[i],
                    Suppressor = members[i + 1],
                };
                _pairs.Add(pair);
            }

            // Odd member out -- solo tracker
            if (members.Count % 2 != 0)
                _singles.Add(members[members.Count - 1]);
        }

        // ---------- Queries --------------------------------------------------

        public BuddyPair GetPair(StealthHuntAI unit)
        {
            for (int i = 0; i < _pairs.Count; i++)
                if (_pairs[i].Tracker == unit || _pairs[i].Suppressor == unit)
                    return _pairs[i];
            return null;
        }

        public BuddyRole GetRole(StealthHuntAI unit)
        {
            var pair = GetPair(unit);
            if (pair == null) return BuddyRole.Tracker; // singles are always Tracker
            return pair.GetRole(unit);
        }

        public StealthHuntAI GetBuddy(StealthHuntAI unit)
        {
            var pair = GetPair(unit);
            return pair?.GetBuddy(unit);
        }

        /// <summary>Signal that Tracker has arrived at cover -- triggers role swap.</summary>
        public void SignalArrived(StealthHuntAI unit)
            => GetPair(unit)?.SignalArrived(unit);

        // ---------- Update ---------------------------------------------------

        public void Update(float dt)
        {
            for (int i = 0; i < _pairs.Count; i++)
                _pairs[i].Update(dt);
        }

        // ---------- Formation offset -----------------------------------------

        /// <summary>
        /// Get the world position Suppressor should hold relative to Tracker.
        /// Called every frame to keep geometric buddy spacing.
        /// </summary>
        public Vector3? GetSupressorPosition(BuddyPair pair)
        {
            if (pair?.Tracker == null) return null;

            var tracker = pair.Tracker;
            Vector3 right = tracker.transform.right;
            Vector3 back = -tracker.transform.forward;

            // Suppressor holds 3m to the right and 2m behind Tracker
            Vector3 ideal = tracker.transform.position
                          + right * 3f
                          + back * 2f;

            // Sample onto NavMesh
            if (UnityEngine.AI.NavMesh.SamplePosition(ideal, out var hit, 3f,
                UnityEngine.AI.NavMesh.AllAreas))
                return hit.position;

            return ideal;
        }
    }

    // ---------- Role enum ----------------------------------------------------

    public enum BuddyRole
    {
        None,
        Tracker,    // pursues threat aggressively
        Suppressor, // covers and holds position
    }
}