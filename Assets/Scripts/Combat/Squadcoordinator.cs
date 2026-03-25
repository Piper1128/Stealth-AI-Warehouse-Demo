using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Squad-level role coordination and flanking positions.
    /// Extracted from TacticalBrain -- owns bounding roles and pincer logic.
    /// Pure data + logic, no MonoBehaviour.
    /// </summary>
    public class SquadCoordinator
    {
        public enum BoundingRole { Advancing, Covering }

        private readonly Dictionary<StealthHuntAI, BoundingRole> _roles
            = new Dictionary<StealthHuntAI, BoundingRole>();
        private readonly List<StealthHuntAI> _members = new List<StealthHuntAI>();

        public SquadStrategySelector Strategy { get; } = new SquadStrategySelector();

        // ---------- Member management ----------------------------------------

        public void Register(StealthHuntAI unit)
        {
            if (!_members.Contains(unit))
            {
                _members.Add(unit);
                Reassign();
            }
        }

        public void Unregister(StealthHuntAI unit)
        {
            _members.Remove(unit);
            _roles.Remove(unit);
        }

        private void Reassign()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i] == null) continue;
                _roles[_members[i]] = i % 2 == 0
                    ? BoundingRole.Advancing : BoundingRole.Covering;
            }
        }

        // ---------- Bounding overwatch ---------------------------------------

        public BoundingRole GetBoundingRole(StealthHuntAI unit)
        {
            if (!_roles.TryGetValue(unit, out var role)) { Reassign(); _roles.TryGetValue(unit, out role); }
            return role;
        }

        public void OnAdvancerReachedCover(StealthHuntAI unit)
        {
            var keys = new List<StealthHuntAI>(_roles.Keys);
            foreach (var k in keys)
                _roles[k] = _roles[k] == BoundingRole.Advancing
                    ? BoundingRole.Covering : BoundingRole.Advancing;
        }

        // ---------- Flanking -------------------------------------------------

        public bool ShouldFlank(StealthHuntAI unit)
        {
            if (_members.Count < 2) return false;
            int idx = _members.IndexOf(unit);
            return idx >= 0 && idx % 2 != 0;
        }

        public Vector3? GetFlankPosition(StealthHuntAI unit, Vector3 estimatedThreatPos)
        {
            Vector3 toThreat = estimatedThreatPos - unit.transform.position;
            toThreat.y = 0f;

            int idx = _members.IndexOf(unit);
            Vector3 flankDir = idx % 2 == 0
                ? Vector3.Cross(toThreat.normalized, Vector3.up)
                : -Vector3.Cross(toThreat.normalized, Vector3.up);

            Vector3 flankPos = estimatedThreatPos
                             + flankDir * 8f + toThreat.normalized * 5f;

            if (!NavMesh.SamplePosition(flankPos, out NavMeshHit hit, 6f, NavMesh.AllAreas))
                return null;

            var path = new NavMeshPath();
            if (!NavMesh.CalculatePath(unit.transform.position, hit.position,
                NavMesh.AllAreas, path)) return null;
            if (path.status != NavMeshPathStatus.PathComplete) return null;

            return hit.position;
        }

        public int MemberCount => _members.Count;
    }
}