using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat.CQB
{
    /// <summary>
    /// Squad-level CQB coordinator. Sits in TacticalBrain.
    /// Selects entry type based on intel and assigns roles to guards.
    ///
    /// Entry types:
    ///   Dynamic    -- player known, rush in simultaneously
    ///   Deliberate -- player unknown, one in first then follow
    ///   Suppress   -- only one guard, hold fatal funnel and watch
    /// </summary>
    public class CQBController
    {
        // ---------- Entry type -----------------------------------------------

        public enum EntryType { None, Dynamic, Deliberate, Suppress }

        public EntryType CurrentEntry { get; private set; } = EntryType.None;
        public EntryPoint ActiveEntry { get; private set; }
        public bool IsActive { get; private set; }

        // ---------- Role assignments -----------------------------------------

        public struct CQBRole
        {
            public StealthHuntAI Unit;
            public bool IsBreacher;   // enters first, goes to DomPointA
            public bool IsFollower;   // enters second, goes to DomPointB
            public bool IsHolder;     // stays outside, covers fatal funnel
            public Vector3 DomTarget;    // assigned point of domination
            public Vector3 StackPos;     // assigned stack position
        }

        private readonly List<CQBRole> _roles = new List<CQBRole>();

        public IReadOnlyList<CQBRole> Roles => _roles;

        // ---------- Signals --------------------------------------------------

        private bool _breacherReady;
        private bool _followerReady;
        private bool _roomCleared;

        public void SignalStackReady(StealthHuntAI unit)
        {
            var role = GetRole(unit);
            if (role == null)
            {
                // No role assigned -- treat as both to unblock
                _breacherReady = true;
                _followerReady = true;
                return;
            }
            if (role.Value.IsBreacher) _breacherReady = true;
            if (role.Value.IsFollower) _followerReady = true;
            if (role.Value.IsHolder) _followerReady = true; // holder counts as follower
        }

        public void SignalRoomCleared(StealthHuntAI unit)
            => _roomCleared = true;

        public bool BothReady => _breacherReady
            && (_followerReady || CurrentEntry == EntryType.Suppress);
        public bool BreacherReady => _breacherReady;
        public bool RoomCleared => _roomCleared;

        // ---------- Evaluate -------------------------------------------------

        /// <summary>
        /// Evaluate whether a CQB entry should be initiated.
        /// Called by StandardCombat when a guard is near an EntryPoint.
        /// </summary>
        public bool EvaluateEntry(Vector3 unitPos, Vector3 threatPos,
                                   float threatConfidence,
                                   List<StealthHuntAI> squadMembers)
        {
            // Guard against same-frame race -- set active immediately
            if (IsActive) return false;
            IsActive = true;

            var ep = EntryPointRegistry.FindBest(unitPos, threatPos);
            if (ep == null) { IsActive = false; return false; }

            float distEpToThreat = Vector3.Distance(ep.transform.position, threatPos);
            if (distEpToThreat > 20f) { IsActive = false; return false; }

            ActiveEntry = ep;
            ep.Occupy(squadMembers.Count > 0 ? squadMembers[0] : null);

            // Choose entry type
            CurrentEntry = threatConfidence > 0.55f
                ? (squadMembers.Count >= 2 ? EntryType.Dynamic : EntryType.Suppress)
                : (squadMembers.Count >= 2 ? EntryType.Deliberate : EntryType.Suppress);

            AssignRoles(squadMembers, ep);
            _breacherReady = false;
            _followerReady = false;
            _roomCleared = false;
            _cqbTimer = 0f;

            return true;
        }

        public void EndEntry()
        {
            ActiveEntry?.Release(null);
            ActiveEntry = null;
            CurrentEntry = EntryType.None;
            IsActive = false;
            _roles.Clear();
        }

        // ---------- Role assignment ------------------------------------------

        private void AssignRoles(List<StealthHuntAI> members, EntryPoint ep)
        {
            _roles.Clear();
            if (members.Count == 0) return;

            if (members.Count == 1)
            {
                _roles.Add(new CQBRole
                {
                    Unit = members[0],
                    IsHolder = true,
                    StackPos = ep.StackLeftPos,
                    DomTarget = ep.DomPosA,
                });
                return;
            }

            // Breacher -- goes to DomPointA (clears left side)
            _roles.Add(new CQBRole
            {
                Unit = members[0],
                IsBreacher = true,
                StackPos = ep.StackLeftPos,
                DomTarget = ep.DomPosA,
            });

            // Follower -- goes to DomPointB (clears right side)
            _roles.Add(new CQBRole
            {
                Unit = members[1],
                IsFollower = true,
                StackPos = ep.StackRightPos,
                DomTarget = ep.DomPosB,
            });

            // Additional guards hold outside
            for (int i = 2; i < members.Count; i++)
            {
                _roles.Add(new CQBRole
                {
                    Unit = members[i],
                    IsHolder = true,
                    StackPos = ep.StackRightPos + Vector3.back * (i - 1) * 1.2f,
                    DomTarget = ep.DomPosB,
                });
            }
        }

        // ---------- Queries --------------------------------------------------

        public CQBRole? GetRole(StealthHuntAI unit)
        {
            for (int i = 0; i < _roles.Count; i++)
                if (_roles[i].Unit == unit) return _roles[i];
            return null;
        }

        // ---------- Global timeout ------------------------------------------

        private float _cqbTimer;
        private const float MaxCQBDuration = 20f;

        public void Tick(float dt)
        {
            if (!IsActive) return;
            _cqbTimer += dt;
            if (_cqbTimer > MaxCQBDuration)
            {
                Debug.Log("[CQB] Timeout -- ending entry");
                EndEntry();
            }
        }

        public bool IsBreacher(StealthHuntAI unit)
            => GetRole(unit)?.IsBreacher ?? false;

        public bool IsFollower(StealthHuntAI unit)
            => GetRole(unit)?.IsFollower ?? false;

        public bool IsHolder(StealthHuntAI unit)
            => GetRole(unit)?.IsHolder ?? false;
    }
}