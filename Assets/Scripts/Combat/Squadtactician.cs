using StealthHuntAI.Combat.CQB;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using static StealthHuntAI.Combat.StandardCombat;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Central squad role controller. Runs on squad leader every 3s or on
    /// significant intel change. Assigns one explicit role to every guard.
    /// Guards execute their assigned role -- no individual role selection.
    ///
    /// Scenarios:
    ///   Search   -- low/no confidence, find player
    ///   Approach -- medium confidence, close in from multiple angles
    ///   Assault  -- high confidence, suppress + flank + advance
    ///   CQB      -- entry point is route to player, breach and clear
    ///   Withdraw -- squad strength critical
    /// </summary>
    public class SquadTactician
    {
        // ---------- Public state ---------------------------------------------

        public enum TacticianScenario
        {
            Search,
            Approach,
            Assault,
            CQB,
            Withdraw,
        }

        public TacticianScenario CurrentScenario { get; private set; }
            = TacticianScenario.Search;

        // ---------- Assigned roles -------------------------------------------

        private readonly Dictionary<int, SquadBlackboard.TacticalRole> _assigned
            = new Dictionary<int, SquadBlackboard.TacticalRole>();

        public SquadBlackboard.TacticalRole GetAssignedRole(StealthHuntAI unit)
        {
            if (_assigned.TryGetValue(unit.GetInstanceID(), out var role))
                return role;
            return SquadBlackboard.TacticalRole.Search;
        }

        // ---------- Re-evaluate triggers -------------------------------------

        private float _evalTimer;
        private float _lastConfidence;
        private int _lastAliveCount = -1; // -1 forces evaluate on first tick
        private const float EvalInterval = 3f;

        private int _squadID = -1;

        public void Tick(float dt, TacticalBrain brain,
                         IReadOnlyList<StealthHuntAI> allUnits, int squadID)
        {
            _squadID = squadID;
            _evalTimer += dt;

            var intel = brain.Intel;
            int aliveCount = CountAlive(allUnits, squadID);

            bool confChanged = Mathf.Abs(intel.Confidence - _lastConfidence) > 0.2f;
            bool guardDied = aliveCount < _lastAliveCount;
            bool timerExpired = _evalTimer >= EvalInterval;

            // Always evaluate immediately on first tick
            if (confChanged || guardDied || timerExpired || _lastAliveCount < 0)
            {
                // Clear assigned roles on death so dead guard's role isnt reused
                if (guardDied) _assigned.Clear();
                Evaluate(brain, allUnits, squadID);
                _lastConfidence = intel.Confidence;
                _lastAliveCount = aliveCount;
                _evalTimer = 0f;
            }
        }

        // ---------- Evaluation -----------------------------------------------

        private void Evaluate(TacticalBrain brain,
                               IReadOnlyList<StealthHuntAI> allUnits, int squadID)
        {
            var members = GetLiveMembers(allUnits, squadID);
            if (members.Count == 0) return;

            var intel = brain.Intel;
            float conf = intel.Confidence;
            float squadStr = (float)members.Count
                           / Mathf.Max(1, CountTotal(allUnits, squadID));

            var board = SquadBlackboard.Get(squadID);
            board?.ClearDestinations(); // fresh destinations each evaluation

            List<SquadBlackboard.RoleSlot> slots;

            if (squadStr < 0.3f)
            {
                slots = BuildSlots_Withdraw(members);
                CurrentScenario = TacticianScenario.Withdraw;
            }
            else if (!intel.HasIntel || conf < 0.15f)
            {
                slots = BuildSlots_Search(members, intel);
                CurrentScenario = TacticianScenario.Search;
            }
            else if (intel.Threat.LastSeenTime < 0f)
            {
                slots = BuildSlots_Cautious(members);
                CurrentScenario = TacticianScenario.Search;
            }
            else
            {
                Vector3 threatPos = intel.EstimatedPos;
                if (IsCQBScenario(members, threatPos, brain))
                {
                    slots = BuildSlots_CQB(members, threatPos, brain);
                    CurrentScenario = TacticianScenario.CQB;
                }
                else if (conf >= 0.5f)
                {
                    slots = BuildSlots_Assault(members, intel);
                    CurrentScenario = TacticianScenario.Assault;
                }
                else
                {
                    slots = BuildSlots_Approach(members, intel);
                    CurrentScenario = TacticianScenario.Approach;
                }
            }

            // Write slots to blackboard -- guards self-claim on next Tick
            board?.WriteRoleSlots(slots);

            // Also maintain _assigned for GetAssignedRole fallback
            // Guards that havent claimed yet get their best slot pre-assigned
            _assigned.Clear();
            if (board != null)
            {
                // Pre-assign: sort members by distance to each slot's ideal pos
                var available = new List<SquadBlackboard.RoleSlot>(slots);
                var sorted = new List<StealthHuntAI>(members);

                foreach (var slot in available)
                {
                    if (sorted.Count == 0) break;
                    sorted.Sort((a, b) =>
                        Vector3.Distance(a.transform.position, slot.IdealPosition)
                        .CompareTo(
                        Vector3.Distance(b.transform.position, slot.IdealPosition)));
                    Assign(sorted[0], slot.Role);
                    sorted.RemoveAt(0);
                }
            }
        }

        // ---------- Slot builders -------------------------------------------
        // Each builder returns a list of RoleSlots with ideal positions.
        // Guards claim slots based on proximity -- emergent coordination.

        private List<SquadBlackboard.RoleSlot> BuildSlots_Withdraw(
            List<StealthHuntAI> members)
        {
            var slots = new List<SquadBlackboard.RoleSlot>();
            foreach (var m in members)
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Withdraw,
                    IdealPosition = m.transform.position, // withdraw from current pos
                });
            return slots;
        }

        private List<SquadBlackboard.RoleSlot> BuildSlots_Cautious(
            List<StealthHuntAI> members)
        {
            var slots = new List<SquadBlackboard.RoleSlot>();
            foreach (var m in members)
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Cautious,
                    IdealPosition = m.transform.position,
                });
            return slots;
        }

        // Maps guard instanceID to assigned search sector angle (kept for TickSearch)
        private readonly Dictionary<int, float> _searchSectors
            = new Dictionary<int, float>();

        public bool HasSearchSector(StealthHuntAI unit)
            => _searchSectors.ContainsKey(unit.GetInstanceID());

        public float GetSearchSectorAngle(StealthHuntAI unit)
        {
            if (_searchSectors.TryGetValue(unit.GetInstanceID(), out float angle))
                return angle;
            return 0f;
        }

        private List<SquadBlackboard.RoleSlot> BuildSlots_Search(
            List<StealthHuntAI> members, SquadIntel intel)
        {
            _searchSectors.Clear();
            var slots = new List<SquadBlackboard.RoleSlot>();
            int count = Mathf.Max(1, members.Count);
            float sector = 360f / count;
            float start = UnityEngine.Random.Range(0f, 360f);
            Vector3 origin = intel.HasIntel
                ? intel.EstimatedPos
                : members[0].transform.position;

            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] == null || members[i].IsDead) continue;
                float angle = start + sector * i;
                _searchSectors[members[i].GetInstanceID()] = angle;

                float rad = angle * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
                Vector3 idealPos = origin + dir * 12f;

                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Search,
                    IdealPosition = idealPos,
                });
            }
            return slots;
        }

        private List<SquadBlackboard.RoleSlot> BuildSlots_Approach(
            List<StealthHuntAI> members, SquadIntel intel)
        {
            var slots = new List<SquadBlackboard.RoleSlot>();
            Vector3 threatPos = intel.EstimatedPos;

            bool directExposed = IsDirectPathExposed(
                members[0].transform.position, threatPos);

            // Advance slot -- directly toward threat
            slots.Add(new SquadBlackboard.RoleSlot
            {
                Role = SquadBlackboard.TacticalRole.Advance,
                IdealPosition = threatPos,
            });

            // Flank slot -- only if path exposed; ideal pos is 90deg offset
            if (directExposed && members.Count >= 2)
            {
                Vector3 toT = (threatPos - members[0].transform.position).normalized;
                Vector3 perp = Vector3.Cross(toT, Vector3.up).normalized;
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Flank,
                    IdealPosition = threatPos + perp * 12f,
                });
            }

            // Rest -- reposition (find angles)
            int remaining = members.Count - slots.Count;
            for (int i = 0; i < remaining; i++)
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Reposition,
                    IdealPosition = threatPos,
                });

            return slots;
        }

        private List<SquadBlackboard.RoleSlot> BuildSlots_Assault(
            List<StealthHuntAI> members, SquadIntel intel)
        {
            var slots = new List<SquadBlackboard.RoleSlot>();
            Vector3 threatPos = intel.EstimatedPos;

            bool anyLOS = false;
            for (int i = 0; i < members.Count; i++)
                if (members[i].Sensor != null && members[i].Sensor.CanSeeTarget)
                { anyLOS = true; break; }

            bool directExposed = IsDirectPathExposed(
                members[0].transform.position, threatPos);

            // Advance
            slots.Add(new SquadBlackboard.RoleSlot
            {
                Role = SquadBlackboard.TacticalRole.Advance,
                IdealPosition = threatPos,
            });

            // Flank -- 90deg offset
            if (directExposed && members.Count >= 2)
            {
                Vector3 toT = (threatPos - members[0].transform.position).normalized;
                Vector3 perp = Vector3.Cross(toT, Vector3.up).normalized;
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Flank,
                    IdealPosition = threatPos + perp * 15f,
                });
            }

            // Suppress -- only if someone has LOS, ideal pos = current pos with LOS
            if (anyLOS && members.Count >= 3)
            {
                StealthHuntAI losUnit = null;
                for (int i = 0; i < members.Count; i++)
                    if (members[i].Sensor != null && members[i].Sensor.CanSeeTarget)
                    { losUnit = members[i]; break; }
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Suppress,
                    IdealPosition = losUnit?.transform.position ?? threatPos,
                });
            }

            // Rest -- reposition
            int remaining = members.Count - slots.Count;
            for (int i = 0; i < remaining; i++)
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Reposition,
                    IdealPosition = threatPos,
                });

            return slots;
        }

        private List<SquadBlackboard.RoleSlot> BuildSlots_CQB(
            List<StealthHuntAI> members, Vector3 threatPos, TacticalBrain brain)
        {
            var slots = new List<SquadBlackboard.RoleSlot>();
            var ep = EntryPointRegistry.FindBest(members[0].transform.position, threatPos);
            if (ep == null) return BuildSlots_Assault(members, brain.Intel);

            var allEps = EntryPointRegistry.FindAllNear(threatPos, 20f);
            EntryPoint rearEp = null;
            for (int i = 0; i < allEps.Count; i++)
                if (allEps[i] != ep) { rearEp = allEps[i]; break; }

            // Breach -- closest to entry
            slots.Add(new SquadBlackboard.RoleSlot
            { Role = SquadBlackboard.TacticalRole.Breach, IdealPosition = ep.StackLeftPos });

            // Follow
            if (members.Count >= 2)
                slots.Add(new SquadBlackboard.RoleSlot
                { Role = SquadBlackboard.TacticalRole.Follow, IdealPosition = ep.StackRightPos });

            // Rear security
            if (rearEp != null && members.Count >= 3)
                slots.Add(new SquadBlackboard.RoleSlot
                { Role = SquadBlackboard.TacticalRole.RearSecurity, IdealPosition = rearEp.StackLeftPos });

            // Rest -- overwatch
            int remaining = members.Count - slots.Count;
            for (int i = 0; i < remaining; i++)
                slots.Add(new SquadBlackboard.RoleSlot
                {
                    Role = SquadBlackboard.TacticalRole.Overwatch,
                    IdealPosition = ep.transform.position + (-ep.transform.forward) * 4f,
                });

            // Start CQB
            if (!brain.CQB.IsActive)
            {
                var sorted = new List<StealthHuntAI>(members);
                sorted.Sort((a, b) =>
                    Vector3.Distance(a.transform.position, ep.transform.position)
                    .CompareTo(
                    Vector3.Distance(b.transform.position, ep.transform.position)));
                brain.CQB.EvaluateEntry(members[0].transform.position,
                    threatPos, brain.Intel.Confidence, sorted);
            }

            return slots;
        }

        // ---------- Helpers --------------------------------------------------

        private void Assign(StealthHuntAI unit, SquadBlackboard.TacticalRole role)
            => _assigned[unit.GetInstanceID()] = role;

        private void AssignAll(List<StealthHuntAI> members, SquadBlackboard.TacticalRole role)
        {
            for (int i = 0; i < members.Count; i++)
                Assign(members[i], role);
        }

        private bool IsCQBScenario(List<StealthHuntAI> members,
                                    Vector3 threatPos, TacticalBrain brain)
        {
            if (brain.CQB.IsActive) return true;

            if (members.Count == 0) return false;
            var ep = EntryPointRegistry.FindBest(members[0].transform.position, threatPos);
            if (ep == null) return false;

            float distEpToThreat = Vector3.Distance(ep.transform.position, threatPos);
            if (distEpToThreat > 12f) return false;

            // Check if direct path is longer than via entry point
            float directDist = NavRouter.PathLength(members[0].transform.position, threatPos);
            float viaEntryDist = NavRouter.PathLength(members[0].transform.position,
                                     ep.transform.position) + distEpToThreat;

            return directDist < 0f || directDist > viaEntryDist * 1.2f;
        }

        /// <summary>
        /// Returns true if the straight line from start to end is mostly exposed.
        /// Samples midpoint and 75% point -- if both are exposed, path is dangerous.
        /// </summary>
        private static bool IsDirectPathExposed(Vector3 from, Vector3 to)
        {
            // Check two points along the path
            Vector3 mid = Vector3.Lerp(from, to, 0.5f);
            Vector3 nearEnd = Vector3.Lerp(from, to, 0.75f);
            bool midExposed = TacticalFilter.IsExposedToThreat(mid, to);
            bool nearExposed = TacticalFilter.IsExposedToThreat(nearEnd, to);
            return midExposed && nearExposed;
        }

        private static List<StealthHuntAI> GetLiveMembers(
            IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            var result = new List<StealthHuntAI>();
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID && !all[i].IsDead)
                    result.Add(all[i]);
            return result;
        }

        private static int CountAlive(IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            int n = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID && !all[i].IsDead)
                    n++;
            return n;
        }

        private static int CountTotal(IReadOnlyList<StealthHuntAI> all, int squadID)
        {
            int n = 0;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == squadID) n++;
            return n;
        }
    }
}