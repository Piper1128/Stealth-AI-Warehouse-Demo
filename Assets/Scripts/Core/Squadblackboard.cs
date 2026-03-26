using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI
{
    /// <summary>
    /// Shared data container for a squad of StealthHuntAI units.
    /// Not a MonoBehaviour -- lives as data inside HuntDirector.
    /// Handles intel sharing, role assignment, and coordinated flanking.
    /// </summary>
    public class SquadBlackboard
    {
        // ---------- Identity --------------------------------------------------

        public int SquadID { get; }
        public int UnitCount => _units.Count;

        // ---------- Shared intel ----------------------------------------------

        /// <summary>Best known position of the target, shared across the squad.</summary>
        public Vector3 SharedLastKnown { get; private set; }

        /// <summary>Confidence in SharedLastKnown (0-1, decays over time).</summary>
        public float SharedConfidence { get; private set; }

        /// <summary>Time.time when intel was last updated.</summary>
        public float LastIntelTime { get; private set; }

        /// <summary>Which unit last reported (for buddy-check logic).</summary>
        public StealthHuntAI LastReporter { get; private set; }

        /// <summary>Shared hide spot candidates from scanner. Sorted by score.</summary>
        public List<HideSpotCandidate> HideSpots { get; private set; }
            = new List<HideSpotCandidate>();

        /// <summary>Flight vector at time of last contact loss.</summary>
        public Vector3 LastFlightVector { get; private set; }

        // ---------- Internal --------------------------------------------------

        private readonly List<StealthHuntAI> _units = new List<StealthHuntAI>();
        private AlertState _squadAlertState = AlertState.Passive;
        private float _lastFlankTime = -999f;
        private float _lastRoleEvalTime = -999f;
        private const float FlankCooldown = 8f;
        private const float RoleEvalInterval = 2f;

        // ---------- Constructor -----------------------------------------------

        public SquadBlackboard(int id)
        {
            SquadID = id;
        }

        // ---------- Unit registry ---------------------------------------------

        public void AddUnit(StealthHuntAI unit)
        {
            if (!_units.Contains(unit))
                _units.Add(unit);
        }

        public void RemoveUnit(StealthHuntAI unit)
        {
            _units.Remove(unit);
        }

        public bool Contains(StealthHuntAI unit) => _units.Contains(unit);

        // ---------- Intel management ------------------------------------------

        /// <summary>
        /// Report a sighted or heard position from one unit to the whole squad.
        /// Other units receive the intel with confidence scaled by distance.
        /// </summary>
        /// <summary>Get blackboard by squad ID.</summary>
        public static SquadBlackboard Get(int squadID)
            => HuntDirector.GetBlackboard(squadID);

        /// <summary>Share intel directly -- used by AlertSquad propagation.</summary>
        public void ShareIntel(Vector3 position, float confidence)
        {
            if (confidence > SharedConfidence)
            {
                SharedLastKnown = position;
                SharedConfidence = confidence;
                LastIntelTime = Time.time;
            }
        }

        public void ReportIntel(StealthHuntAI reporter, Vector3 position, float confidence)
        {
            if (confidence <= SharedConfidence * 0.5f) return;

            SharedLastKnown = position;
            SharedConfidence = confidence;
            LastIntelTime = Time.time;
            LastReporter = reporter;

            for (int i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                if (unit == null || unit == reporter) continue;

                float dist = Vector3.Distance(reporter.transform.position,
                                                       unit.transform.position);
                float falloff = Mathf.Clamp01(1f - dist / 30f);
                float receivedConf = confidence * falloff * 0.7f;

                unit.ReceiveSquadIntel(position, receivedConf);
            }
        }

        /// <summary>Called by HuntDirector every 0.5s to age intel.</summary>
        public void DecayIntel(float decayTimeFull)
        {
            if (SharedConfidence <= 0f) return;

            float age = Time.time - LastIntelTime;
            SharedConfidence = Mathf.Clamp01(1f - age / decayTimeFull);
        }

        // ---------- Role evaluation -------------------------------------------

        /// <summary>
        /// Dynamically assigns roles based on position relative to target.
        /// Manual roles set in the inspector are never overwritten.
        /// </summary>
        public void EvaluateRoles(StealthTarget target)
        {
            if (target == null || _units.Count == 0) return;
            if (_squadAlertState == AlertState.Passive) return;
            if (SharedConfidence <= 0.1f) return;

            // Rate limit role evaluation -- no need to run every frame
            if (Time.time - _lastRoleEvalTime < RoleEvalInterval) return;
            _lastRoleEvalTime = Time.time;

            Vector3 targetPos = SharedLastKnown;

            var candidates = new List<CandidateEntry>();

            for (int i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                if (unit == null) continue;
                if (unit.manualRole != SquadRole.Dynamic) continue;

                float dist = Vector3.Distance(unit.transform.position, targetPos);
                candidates.Add(new CandidateEntry(unit, dist));
            }

            if (candidates.Count == 0) return;

            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            bool hasTracker = false;
            bool hasFlanker = false;
            bool hasOverwatch = false;
            bool hasBlocker = false;

            for (int i = 0; i < candidates.Count; i++)
            {
                var unit = candidates[i].Unit;
                SquadRole role;

                if (!hasTracker)
                {
                    role = SquadRole.Tracker;
                    hasTracker = true;
                }
                else if (!hasFlanker && candidates.Count >= 2
                      && Time.time - _lastFlankTime >= FlankCooldown)
                {
                    role = SquadRole.Flanker;
                    hasFlanker = true;
                    _lastFlankTime = Time.time;
                    AssignFlankPoint(unit, targetPos);
                }
                else if (!hasOverwatch && candidates.Count >= 3)
                {
                    role = SquadRole.Overwatch;
                    hasOverwatch = true;
                }
                else if (!hasBlocker && candidates.Count >= 4)
                {
                    role = SquadRole.Blocker;
                    hasBlocker = true;
                    AssignBlockPoint(unit, targetPos);
                }
                else
                {
                    role = SquadRole.Tracker;
                }

                unit.AssignRole(role);
            }
        }

        public void OnUnitStateChanged(StealthHuntAI unit,
                                        AlertState newAlert, SubState newSub)
        {
            if (newAlert > _squadAlertState)
                _squadAlertState = newAlert;

            if (newAlert == AlertState.Hostile && unit.LastKnownPosition.HasValue)
                ReportIntel(unit, unit.LastKnownPosition.Value, 0.9f);

            if (newAlert == AlertState.Passive)
            {
                bool anyActive = false;
                for (int i = 0; i < _units.Count; i++)
                {
                    if (_units[i] != null &&
                        _units[i].CurrentAlertState != AlertState.Passive)
                    {
                        anyActive = true;
                        break;
                    }
                }

                if (!anyActive)
                {
                    _squadAlertState = AlertState.Passive;
                    ClearHideSpots();
                }
            }
        }

        // ---------- Hide spot management --------------------------------------

        /// <summary>
        /// Called by a unit when its scanner finishes.
        /// Merges new candidates into the shared list, deduplicating by proximity.
        /// </summary>
        public void SubmitHideSpots(List<HideSpotCandidate> candidates, Vector3 flightVector)
        {
            if (candidates == null || candidates.Count == 0) return;

            LastFlightVector = flightVector;

            foreach (var c in candidates)
            {
                // Deduplicate -- skip if we already have a spot within 1.5m
                bool duplicate = false;
                for (int i = 0; i < HideSpots.Count; i++)
                {
                    if (Vector3.Distance(HideSpots[i].Position, c.Position) < 1.5f)
                    {
                        // Keep the higher scored version
                        if (c.TotalScore > HideSpots[i].TotalScore)
                        {
                            HideSpots[i] = c;
                        }
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    HideSpots.Add(c);
            }

            // Re-sort after merge
            HideSpots.Sort((a, b) => b.TotalScore.CompareTo(a.TotalScore));

            // Cap list size
            if (HideSpots.Count > 12)
                HideSpots.RemoveRange(12, HideSpots.Count - 12);
        }

        /// <summary>
        /// Claim the next uninvestigated hide spot for a unit.
        /// Returns true if a spot was found and assigned.
        /// </summary>
        public bool ClaimNextHideSpot(out HideSpotCandidate spot)
        {
            for (int i = 0; i < HideSpots.Count; i++)
            {
                if (!HideSpots[i].Investigated)
                {
                    // Mark as claimed (copy struct back)
                    var claimed = HideSpots[i];
                    claimed.Investigated = true;
                    HideSpots[i] = claimed;

                    spot = claimed;
                    return true;
                }
            }

            spot = default;
            return false;
        }

        /// <summary>Clear hide spots when squad returns to passive.</summary>
        public void ClearHideSpots()
        {
            HideSpots.Clear();
            LastFlightVector = Vector3.zero;
        }

        // ---------- Search coordination ---------------------------------------

        private readonly Dictionary<StealthHuntAI, Vector3> _searchingUnits
            = new Dictionary<StealthHuntAI, Vector3>();

        /// <summary>
        /// Register a unit as actively searching around a center point.
        /// Other units can query this to avoid overlapping search areas.
        /// </summary>
        public void RegisterSearchUnit(StealthHuntAI unit, Vector3 searchCenter)
        {
            _searchingUnits[unit] = searchCenter;
        }

        /// <summary>Remove a unit from active searchers when it finishes.</summary>
        public void UnregisterSearchUnit(StealthHuntAI unit)
        {
            _searchingUnits.Remove(unit);
        }

        /// <summary>
        /// Returns true if a candidate search point is already being covered
        /// by another unit in this squad within the given radius.
        /// Use this to avoid two units searching the same area.
        /// </summary>
        public bool IsPointCovered(Vector3 point, float radius)
        {
            foreach (var pair in _searchingUnits)
            {
                if (Vector3.Distance(pair.Value, point) < radius)
                    return true;
            }
            return false;
        }


        // ---------- Role slot coordination -----------------------------------
        // SquadTactician writes available slots. Guards claim them based on
        // position -- closest guard to ideal position wins the slot.
        // This replaces index-based role assignment with emergent coordination.


        /// <summary>
        /// Combat role for blackboard coordination.
        /// Mirrors StandardCombat.CombatRole -- kept separate to avoid
        /// Core -> Combat assembly dependency.
        /// </summary>
        public enum TacticalRole
        {
            Advance, Flank, Suppress, Cover, Cautious,
            Reposition, Search, Overwatch, RearSecurity,
            Breach, Follow, Withdraw, CQB, Idle
        }

        public class RoleSlot
        {
            public TacticalRole Role;
            public StealthHuntAI ClaimedBy;   // null = available
            public Vector3 IdealPosition;     // where this role should go
            public float ClaimedAt;
            public bool IsAvailable => ClaimedBy == null
                || ClaimedBy.IsDead
                || Time.time - ClaimedAt > 8f; // expire stale claims
        }

        private readonly List<RoleSlot> _roleSlots = new List<RoleSlot>();

        /// <summary>
        /// Called by SquadTactician each evaluation.
        /// Replaces all current slots with new ones for the scenario.
        /// Dead guard claims are cleared automatically via IsAvailable.
        /// </summary>
        public void WriteRoleSlots(List<RoleSlot> slots)
        {
            _roleSlots.Clear();
            _roleSlots.AddRange(slots);
        }

        /// <summary>
        /// Guard tries to claim the best available slot for its position.
        /// Returns the claimed slot or null if none available.
        /// Guards closer to the ideal position score higher.
        /// </summary>
        public RoleSlot ClaimBestSlot(StealthHuntAI unit)
        {
            RoleSlot best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < _roleSlots.Count; i++)
            {
                var slot = _roleSlots[i];
                if (!slot.IsAvailable) continue;
                // Already claimed by this unit -- keep it
                if (slot.ClaimedBy == unit) return slot;

                float score = ScoreSlotForUnit(slot, unit);
                if (score > bestScore) { bestScore = score; best = slot; }
            }

            if (best != null)
            {
                best.ClaimedBy = unit;
                best.ClaimedAt = Time.time;
            }
            return best;
        }

        /// <summary>Release this unit's claim when it changes role or dies.</summary>
        public void ReleaseSlot(StealthHuntAI unit)
        {
            for (int i = 0; i < _roleSlots.Count; i++)
                if (_roleSlots[i].ClaimedBy == unit)
                    _roleSlots[i].ClaimedBy = null;
        }

        /// <summary>Get the slot currently claimed by this unit.</summary>
        public RoleSlot GetClaimedSlot(StealthHuntAI unit)
        {
            for (int i = 0; i < _roleSlots.Count; i++)
                if (_roleSlots[i].ClaimedBy == unit && !_roleSlots[i].IsAvailable == false)
                    return _roleSlots[i];
            return null;
        }

        public IReadOnlyList<RoleSlot> RoleSlots => _roleSlots;

        private static float ScoreSlotForUnit(RoleSlot slot, StealthHuntAI unit)
        {
            float dist = slot.IdealPosition != Vector3.zero
                ? Vector3.Distance(unit.transform.position, slot.IdealPosition)
                : 0f;
            // Closer is better -- max score at 0m, decays over 30m
            return 1f - Mathf.Clamp01(dist / 30f);
        }

        // ---------- Destination registry -------------------------------------
        // Guards register their movement destinations so others avoid overlap.

        private readonly Dictionary<StealthHuntAI, Vector3> _destinations
            = new Dictionary<StealthHuntAI, Vector3>();

        /// <summary>Register where this guard is moving to.</summary>
        public void RegisterDestination(StealthHuntAI unit, Vector3 dest)
            => _destinations[unit] = dest;

        /// <summary>Unregister when guard stops or changes role.</summary>
        public void UnregisterDestination(StealthHuntAI unit)
            => _destinations.Remove(unit);

        /// <summary>
        /// Returns true if another guard is already moving to within radius of dest.
        /// </summary>
        public bool IsDestinationClaimed(StealthHuntAI unit, Vector3 dest, float radius = 3f)
        {
            foreach (var pair in _destinations)
            {
                if (pair.Key == unit || pair.Key == null || pair.Key.IsDead) continue;
                if (Vector3.Distance(pair.Value, dest) < radius) return true;
            }
            return false;
        }

        /// <summary>Clear all destinations -- called on squad reset.</summary>
        public void ClearDestinations() => _destinations.Clear();

        // ---------- Spatial helpers -------------------------------------------

        private void AssignFlankPoint(StealthHuntAI unit, Vector3 targetPos)
        {
            Vector3 toTarget = (targetPos - unit.transform.position).normalized;
            Vector3 flankDir = Vector3.Cross(toTarget, Vector3.up).normalized;

            if (Random.value > 0.5f) flankDir = -flankDir;

            Vector3 flankPoint = targetPos + flankDir * 5f
                               + toTarget * Random.Range(-2f, 2f);

            if (NavMeshHelper.Sample(flankPoint, 6f, out Vector3 snapped))
                unit.SetFlankDestination(snapped);
        }

        private void AssignBlockPoint(StealthHuntAI unit, Vector3 targetPos)
        {
            Vector3 trackerPos = GetTrackerPosition();
            Vector3 behindTarget = targetPos + (targetPos - trackerPos) * 2f;

            if (NavMeshHelper.Sample(behindTarget, 8f, out Vector3 snapped))
                unit.SetFlankDestination(snapped);
        }

        private Vector3 GetTrackerPosition()
        {
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i] != null && _units[i].ActiveRole == SquadRole.Tracker)
                    return _units[i].transform.position;
            }
            return _units.Count > 0 ? _units[0].transform.position : Vector3.zero;
        }

        // ---------- Helper struct ---------------------------------------------

        private struct CandidateEntry
        {
            public StealthHuntAI Unit;
            public float Distance;

            public CandidateEntry(StealthHuntAI unit, float distance)
            {
                Unit = unit;
                Distance = distance;
            }
        }
    }
}