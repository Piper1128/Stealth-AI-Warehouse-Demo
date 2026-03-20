using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Squad-level tactical coordination for combat.
    /// One TacticalBrain per squad -- shared between all StandardCombat instances in the squad.
    /// Coordinates bounding overwatch, pincer movement and role assignment.
    /// </summary>
    public class TacticalBrain
    {
        // ---------- Shared threat model --------------------------------------

        /// <summary>Squad-level threat model -- best available intel from any squad member.</summary>
        public ThreatModel SharedThreat { get; } = new ThreatModel();

        // ---------- Bounding overwatch ---------------------------------------

        public enum BoundingRole { Advancing, Covering }

        private readonly Dictionary<StealthHuntAI, BoundingRole> _boundingRoles
            = new Dictionary<StealthHuntAI, BoundingRole>();

        private float _boundingSwapTimer;
        private const float BoundingSwapInterval = 3f;

        /// <summary>Get this unit's current bounding role.</summary>
        public BoundingRole GetBoundingRole(StealthHuntAI unit)
        {
            if (!_boundingRoles.TryGetValue(unit, out var role))
            {
                AssignInitialRoles();
                _boundingRoles.TryGetValue(unit, out role);
            }
            return role;
        }

        private readonly List<StealthHuntAI> _members = new List<StealthHuntAI>();

        public void RegisterMember(StealthHuntAI unit)
        {
            if (!_members.Contains(unit))
            {
                _members.Add(unit);
                AssignInitialRoles();
            }
        }

        public void UnregisterMember(StealthHuntAI unit)
        {
            _members.Remove(unit);
            _boundingRoles.Remove(unit);
        }

        private void AssignInitialRoles()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i] == null) continue;
                _boundingRoles[_members[i]] = i % 2 == 0
                    ? BoundingRole.Advancing
                    : BoundingRole.Covering;
            }
        }

        /// <summary>
        /// Update bounding -- swap roles periodically so both units advance.
        /// Call from the advancing unit when it reaches cover.
        /// </summary>
        public void OnAdvancerReachedCover(StealthHuntAI unit)
        {
            // Swap all roles
            var keys = new List<StealthHuntAI>(_boundingRoles.Keys);
            foreach (var k in keys)
            {
                _boundingRoles[k] = _boundingRoles[k] == BoundingRole.Advancing
                    ? BoundingRole.Covering
                    : BoundingRole.Advancing;
            }
        }

        // ---------- Threat sharing -------------------------------------------

        /// <summary>Report threat intel from a unit that can see player.</summary>
        public void ReportThreat(StealthHuntAI reporter, Vector3 playerPos,
                                  Vector3 playerVel, float confidence)
        {
            SharedThreat.ReceiveIntel(playerPos, playerVel, confidence);
        }

        /// <summary>Update shared threat when no unit can see player.</summary>
        public void UpdateNoSight()
        {
            SharedThreat.UpdateWithoutSight();
        }

        // ---------- Tactical position selection ------------------------------

        /// <summary>
        /// Find best tactical cover position for a unit given its role.
        /// Returns null if no suitable position found.
        /// </summary>
        public CoverPoint GetTacticalCover(StealthHuntAI unit, SquadRole role,
                                            CoverWeights weights)
        {
            if (!SharedThreat.HasIntel) return null;

            Vector3 targetPos = SharedThreat.EstimatedPosition;
            var scored = CoverEvaluator.Evaluate(unit, targetPos, role, weights);

            if (scored.Count == 0) return null;

            // For bounding -- advancer picks cover closest to threat
            // Coverer picks cover with good LOS to cover advancer's movement
            var role2 = GetBoundingRole(unit);
            if (role2 == BoundingRole.Advancing)
            {
                // Pick cover that advances toward threat
                return GetAdvancingCover(scored, unit, targetPos);
            }
            else
            {
                // Pick cover with good visibility to cover teammate
                return scored[0].Point;
            }
        }

        private CoverPoint GetAdvancingCover(List<ScoredCover> scored,
                                              StealthHuntAI unit, Vector3 targetPos)
        {
            float unitDist = Vector3.Distance(unit.transform.position, targetPos);
            CoverPoint best = null;
            float bestScore = -1f;

            foreach (var sc in scored)
            {
                float coverDist = Vector3.Distance(sc.Point.transform.position, targetPos);

                // Prefer cover that is closer to target than current position
                float advanceBonus = coverDist < unitDist ? 1.5f : 0.5f;
                float total = sc.Score * advanceBonus;

                if (total > bestScore) { bestScore = total; best = sc.Point; }
            }

            return best ?? scored[0].Point;
        }

        // ---------- Pincer coordination --------------------------------------

        /// <summary>
        /// Returns true if this unit should flank rather than advance directly.
        /// True for roughly half the squad to create pincer movement.
        /// </summary>
        public bool ShouldFlank(StealthHuntAI unit)
        {
            if (_members.Count < 2) return false;
            int idx = _members.IndexOf(unit);
            return idx >= 0 && idx % 2 != 0;
        }

        /// <summary>
        /// Get a flanking position -- to the side of the estimated threat position.
        /// </summary>
        public Vector3? GetFlankPosition(StealthHuntAI unit)
        {
            if (!SharedThreat.HasIntel) return null;

            Vector3 toThreat = SharedThreat.EstimatedPosition - unit.transform.position;
            toThreat.y = 0f;

            // Flank direction -- perpendicular to threat direction
            // Alternate left/right based on unit index
            int idx = _members.IndexOf(unit);
            Vector3 flankDir = idx % 2 == 0
                ? Vector3.Cross(toThreat.normalized, Vector3.up)
                : -Vector3.Cross(toThreat.normalized, Vector3.up);

            Vector3 flankPos = SharedThreat.EstimatedPosition
                             + flankDir * 8f
                             + toThreat.normalized * 5f;

            if (NavMesh.SamplePosition(flankPos, out NavMeshHit hit, 6f, NavMesh.AllAreas))
                return hit.position;

            return null;
        }

        // ---------- Static registry ------------------------------------------

        private static readonly Dictionary<int, TacticalBrain> _brains
            = new Dictionary<int, TacticalBrain>();

        public static TacticalBrain GetOrCreate(int squadID)
        {
            if (!_brains.TryGetValue(squadID, out var brain))
            {
                brain = new TacticalBrain();
                _brains[squadID] = brain;
            }
            return brain;
        }

        public static void Clear()
        {
            _brains.Clear();
        }
    }
}