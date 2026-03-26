using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using StealthHuntAI.Combat.CQB;

namespace StealthHuntAI.Combat
{
    public class StandardCombat : MonoBehaviour, ICombatBehaviour
    {
        [Range(0.1f, 4f)] public float RoleInterval = 3f;
        [Range(0.5f, 2f)] public float SpeedMultiplier = 1.4f;

        public bool WantsControl { get; private set; }
        public string CurrentStateName => _role.ToString();
        public string CurrentPlanName => _role + " t=" + _roleTimer.ToString("F1") + "s";
        public string CurrentStrategy => _brain?.Strategy.ToString() ?? "None";

        public enum Goal { Idle, AdvanceTo, Flank, Suppress, HoldAndFire, Search, Withdraw }
        public Goal CurrentGoal { get; private set; }
        public bool IsInCover { get; private set; }

        public enum CombatRole
        {
            Advance, Flank, Suppress, Cover, Cautious,
            Reposition, Search, Overwatch, RearSecurity,
            Breach, Follow, Withdraw, CQB, Idle
        }

        private StealthHuntAI _ai;
        private TacticalBrain _brain;
        private CombatEventBus _events;
        private ThreatModel _threat => _brain?.Intel?.Threat;

        internal CombatRole _role = CombatRole.Idle;
        private SquadTactician.TacticianScenario _lastScenario
            = (SquadTactician.TacticianScenario)(-1); // invalid -- forces first claim // internal for squad coordination
        private float _roleTimer;
        private float _roleMaxTime = 8f;
        private Vector3 _roleDestination;
        private bool _roleDestSet;
        private List<Vector3> _waypoints;
        private int _waypointIdx;
        private float _shootTimer;
        private float _peekTimer;
        private bool _atCover;
        private Vector3 _coverPos;
        private TacticalSpot _reservedSpot;
        private Vector3 _lastPos;
        private float _stuckTimer;
        private float _blockedTimer;
        private readonly HashSet<Vector3Int> _blockedCells = new HashSet<Vector3Int>();
        private float _combatShootTimer;
        private GoapAction _cqbAction;

        // Suppress state
        private float _suppressBurstTimer;
        private int _suppressBurst;

        // Cautious state
        private bool _cautiousWaiting;
        private float _cautiousWaitTimer;

        // ---------- ICombatBehaviour -----------------------------------------

        public void OnEnterCombat(StealthHuntAI ai)
        {
            WantsControl = true;
            _ai = ai;

            // Fast re-entry after YieldToCore
            if (_brain != null) { _brain.RegisterMember(ai); return; }

            _brain = TacticalBrain.GetOrCreate(ai.squadID);
            _brain.RegisterMember(ai);
            _events = CombatEventBus.Get(ai);

            var sharedThreat = _brain.Intel.Threat;
            var board = SquadBlackboard.Get(ai.squadID);
            if (board != null && board.SharedConfidence > 0.05f)
                sharedThreat.ReceiveIntel(board.SharedLastKnown, Vector3.zero,
                                          board.SharedConfidence);
            var target = ai.GetTarget();
            if (target != null && ai.Sensor != null && ai.Sensor.CanSeeTarget)
                sharedThreat.UpdateWithSight(target.Position, target.Velocity);
            if (sharedThreat.EstimatedPosition == Vector3.zero)
                sharedThreat.ReceiveIntel(
                    ai.transform.position + ai.transform.forward * 10f,
                    Vector3.zero, 0.1f);

            SetRole(CombatRole.Search);
        }

        public void OnExitCombat(StealthHuntAI ai)
        {
            WantsControl = false;
            _brain?.UnregisterMember(ai);
            ReleaseCoverSpot();
            ai.CombatRestoreRotation();
        }

        public void OnNearShot(Vector3 shotOrigin)
        {
            if (_threat == null) return; // not yet in combat
            _threat.RegisterShotFrom(shotOrigin,
                (shotOrigin - _ai.transform.position).normalized);
            bool isMoving = _role == CombatRole.Advance
                         || _role == CombatRole.Flank
                         || _role == CombatRole.Cautious
                         || _role == CombatRole.Reposition;
            if (!isMoving)
                ForceRole(_threat.HasLOS ? CombatRole.Cover : CombatRole.Reposition);
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            UpdateThreat();
            ProcessEvents();
            CheckStuck();

            TickCombatLoop();

            // Only yield when no longer Hostile -- combat pack owns all Hostile states
            if (_ai.CurrentAlertState != AlertState.Hostile)
            { YieldToCore(); return; }

            _roleTimer += Time.deltaTime;

            // Apply role only when Tactician scenario changes or guard has no role
            // Guards keep their role until next Tactician evaluation -- no per-frame churn
            if (_brain != null)
            {
                var scenario = _brain.Tactician.CurrentScenario;
                // Re-claim if: scenario changed, no role, or Search with no sector
                bool noSector = _role == CombatRole.Search
                    && !_brain.Tactician.HasSearchSector(_ai);
                if (scenario != _lastScenario || _role == CombatRole.Idle || noSector)
                {
                    _lastScenario = scenario;
                    var board = SquadBlackboard.Get(_ai.squadID);
                    var slot = board?.ClaimBestSlot(_ai);
                    SquadBlackboard.TacticalRole tacRole = slot != null
                        ? slot.Role
                        : _brain.Tactician.GetAssignedRole(_ai);
                    var assigned = FromTactical(tacRole);
                    if (assigned != _role) ForceRole(assigned);
                }
            }

            // Search: reset dest periodically to pick new sector
            // Idle: trigger a fresh claim
            if (_role == CombatRole.Search && _roleTimer >= _roleMaxTime)
            { _roleDestSet = false; _roleTimer = 0f; }
            else if (_role == CombatRole.Idle)
            { _lastScenario = (SquadTactician.TacticianScenario)(-1); }

            TickRole();

            HuntDirector.RegisterHeat(ai.transform.position, 0.04f * Time.deltaTime);
        }

        // ---------- Combat loop ----------------------------------------------

        private void TickCombatLoop()
        {
            if (_threat == null) return;
            if (_threat.HasLOS)
            {
                _combatShootTimer += Time.deltaTime;
                if (_combatShootTimer >= 0.25f)
                {
                    _combatShootTimer = 0f;
                    _ai.CombatFaceToward(_threat.EstimatedPosition, 300f);
                    ShootAt(_threat.EstimatedPosition);
                }
            }
            else
            {
                _combatShootTimer = 0f;
            }
        }

        // ---------- Role selection -------------------------------------------

        private void SelectRole()
        {
            if (_brain == null) return;
            // Try to claim a role slot from the blackboard
            var board = SquadBlackboard.Get(_ai.squadID);
            if (board != null)
            {
                var slot = board.ClaimBestSlot(_ai);
                if (slot != null)
                {
                    SetRole(FromTactical(slot.Role));
                    return;
                }
            }
            // Fallback to Tactician assignment
            SetRole(FromTactical(_brain.Tactician.GetAssignedRole(_ai)));
        }

        public void ForceRole(CombatRole role)
        {
            if (_role == role) return;
            // Release destination and slot on role change
            SquadBlackboard.Get(_ai?.squadID ?? -1)?.UnregisterDestination(_ai);
            SquadBlackboard.Get(_ai?.squadID ?? -1)?.ReleaseSlot(_ai); // no reset if same
            ReleaseCoverSpot();
            _role = role;
            _roleTimer = 0f;
            _roleDestSet = false;
            _waypoints = null;
            _waypointIdx = 0;
            _atCover = false;
            _cautiousWaiting = false;
            _cqbAction = null;
            _roleMaxTime = role switch
            {
                CombatRole.Suppress => 4f,
                CombatRole.Cover => 3f,
                CombatRole.Advance => 12f,
                CombatRole.Flank => 15f,
                CombatRole.Cautious => 10f,
                CombatRole.Reposition => 8f,
                CombatRole.CQB => 30f,
                CombatRole.Search => 20f,
                CombatRole.Withdraw => 15f,
                _ => 8f,
            };
            CurrentGoal = role switch
            {
                CombatRole.Advance => Goal.AdvanceTo,
                CombatRole.Flank => Goal.Flank,
                CombatRole.Suppress => Goal.Suppress,
                CombatRole.Reposition => Goal.AdvanceTo,
                CombatRole.Cover => Goal.HoldAndFire,
                CombatRole.Cautious => Goal.AdvanceTo,
                CombatRole.Search => Goal.Search,
                CombatRole.Overwatch => Goal.HoldAndFire,
                CombatRole.RearSecurity => Goal.HoldAndFire,
                CombatRole.Breach => Goal.AdvanceTo,
                CombatRole.Follow => Goal.AdvanceTo,
                CombatRole.Withdraw => Goal.Withdraw,
                _ => Goal.Idle,
            };
        }

        private void SetRole(CombatRole role)
        {
            if (_role == role && _roleTimer < _roleMaxTime * 0.8f) return;
            ForceRole(role);
        }

        // ---------- Role execution -------------------------------------------

        private void TickRole()
        {
            switch (_role)
            {
                case CombatRole.Advance: TickAdvance(); break;
                case CombatRole.Flank: TickFlank(); break;
                case CombatRole.Suppress: TickSuppress(); break;
                case CombatRole.Cover: TickCover(); break;
                case CombatRole.Cautious: TickCautious(); break;
                case CombatRole.Reposition: TickReposition(); break;
                case CombatRole.Search: TickSearch(); break;
                case CombatRole.Overwatch: TickOverwatch(); break;
                case CombatRole.RearSecurity: TickRearSecurity(); break;
                case CombatRole.Breach: TickCQB(); break;
                case CombatRole.Follow: TickCQB(); break;
                case CombatRole.Withdraw: TickWithdraw(); break;
                case CombatRole.CQB: TickCQB(); break;
                case CombatRole.Idle: SelectRole(); break;
            }
        }

        // --- Advance ---------------------------------------------------------

        private void TickAdvance()
        {
            // Stale intel -- switch to cautious search instead of blind advance
            if (_threat.TimeSinceSeen > 15f && !_threat.HasLOS)
            { ForceRole(CombatRole.Cautious); return; }

            Vector3 dest = GetRoutingDest();
            if (!_roleDestSet || Vector3.Distance(_roleDestination, dest) > 6f)
            {
                int idx = GetSquadIndex();
                int count = GetSquadCount();
                Vector3 toT = (dest - _ai.transform.position);
                toT.y = 0f;
                Vector3 perp = Vector3.Cross(toT.normalized, Vector3.up);
                float spread = Mathf.Approximately(toT.magnitude, 0f) ? 0f
                               : (idx - count * 0.5f) * 3f;
                Vector3 rawDest = dest + perp * spread;
                // Sample onto NavMesh -- prevents destination inside walls
                _roleDestination = NavMesh.SamplePosition(rawDest, out var navHit, 4f,
                    NavMesh.AllAreas) ? navHit.position : dest;
                // Pass actual threat pos so cover-to-cover routing is correct
                Vector3 threatForRoute = _threat?.EstimatedPosition ?? _roleDestination;
                _waypoints = TacticalPathfinder.BuildAdvanceRoute(_ai, threatForRoute);
                if (_waypoints == null || _waypoints.Count == 0)
                    _waypoints = BuildDirectPath(_roleDestination);
                _waypointIdx = 0;
                _roleDestSet = true;
                // Register destination so other guards avoid same spot
                SquadBlackboard.Get(_ai.squadID)
                    ?.RegisterDestination(_ai, _roleDestination);
            }

            // F.E.A.R. coherency: wait if too far ahead of squad anchor
            if (_brain != null)
            {
                var anchor = _brain.SquadAnchor == Vector3.zero
                    ? _ai.transform.position  // not initialized yet -- skip check
                    : _brain.SquadAnchor;
                float distFromAnchor = Vector3.Distance(
                    _ai.transform.position, anchor);
                if (distFromAnchor > _brain.CoherencyRadius)
                {
                    // Too far ahead -- hold position and let others catch up
                    _ai.CombatStop();
                    _ai.CombatFaceToward(GetRoutingDest(), 80f);
                    if (_threat != null && _threat.HasLOS)
                        ShootAt(_threat.EstimatedPosition);
                    return;
                }
            }

            if (_waypoints != null && _waypoints.Count > 0)
            {
                if (_waypointIdx < _waypoints.Count)
                    _ai.CombatMoveTo(_waypoints[_waypointIdx], SpeedMultiplier);
                bool done = TacticalPathfinder.FollowWaypoints(_ai, _waypoints,
                    ref _waypointIdx);
                if (done) _roleTimer = _roleMaxTime;
            }
            else
            {
                // No waypoints -- direct move, reset dest if blocked
                if (IsDestinationBlocked(_roleDestination))
                    _roleDestSet = false;
                else
                    _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);
            }

            // POINT 5: cohesion -- dont advance too far from squad
            if (_brain != null && Vector3.Distance(_ai.transform.position, _brain.SquadAnchor) > _brain.CoherencyRadius * 1.5f)
            { _roleDestSet = false; } // recalculate closer dest

            if (Vector3.Distance(_ai.transform.position, _roleDestination) < 4f)
                _roleTimer = _roleMaxTime;
        }

        // --- Flank -----------------------------------------------------------

        private void TickFlank()
        {
            if (!_roleDestSet)
            {
                Vector3 threatPos = GetRoutingDest();
                var ep = EntryPointRegistry.FindBest(
                                        _ai.transform.position, threatPos);

                if (ep != null && Vector3.Distance(ep.transform.position, threatPos) < 20f)
                {
                    int idx = GetSquadIndex();
                    Vector3 stackPos = idx % 2 == 0 ? ep.StackLeftPos : ep.StackRightPos;
                    _roleDestination = stackPos;
                    _waypoints = TacticalPathfinder.BuildAdvanceRoute(_ai, stackPos);
                }
                else
                {
                    _waypoints = TacticalPathfinder.BuildFlankRoute(_ai, threatPos);
                    _roleDestination = _waypoints != null && _waypoints.Count > 0
                        ? _waypoints[_waypoints.Count - 1] : threatPos;
                }
                _waypointIdx = 0;
                _roleDestSet = true;
                SquadBlackboard.Get(_ai.squadID)?.RegisterDestination(_ai, _roleDestination);
            }

            if (_waypoints != null && _waypoints.Count > 0)
            {
                if (_waypointIdx < _waypoints.Count)
                    _ai.CombatMoveTo(_waypoints[_waypointIdx], SpeedMultiplier);
                TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx);
            }
            else
            {
                if (IsDestinationBlocked(_roleDestination))
                { _roleDestSet = false; return; }
                _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);
            }

            if (Vector3.Distance(_ai.transform.position, _roleDestination) < 2f)
                ForceRole(CombatRole.Cover);
        }

        // --- Suppress --------------------------------------------------------

        private void TickSuppress()
        {
            IsInCover = false;
            Vector3 target = GetBestKnownPos();
            _ai.CombatFaceToward(target, 200f);

            Vector3 toT = target - _ai.transform.position;
            bool blocked = Physics.Raycast(
                _ai.transform.position + Vector3.up * 1.5f,
                toT.normalized, toT.magnitude * 0.6f,
                LayerMask.GetMask("Default", "Environment"));

            if (!blocked)
            {
                // Have firing angle -- stop and suppress
                _ai.CombatStop();
                _suppressBurstTimer += Time.deltaTime;
                if (_suppressBurstTimer > 0.35f)
                {
                    _suppressBurstTimer = 0f;
                    ShootAt(target);
                    if (++_suppressBurst >= 3)
                    {
                        _suppressBurst = 0;
                        ForceRole(CombatRole.Reposition);
                    }
                }
            }
            else
            {
                // No firing angle -- immediately reposition for better angle
                ForceRole(CombatRole.Reposition);
            }
        }

        // --- Cover -----------------------------------------------------------
        // Situation-based: reposition when exposed too long, lose LOS, or take damage.

        private float _exposedInCoverTimer;
        private float _repositionWindow;

        private void TickCover()
        {
            if (!_atCover)
            {
                if (!_roleDestSet)
                {
                    var spot = FindCoverSpot();
                    if (spot != null)
                    {
                        _coverPos = spot.Position;
                        _reservedSpot = spot;
                        _roleDestSet = true;
                        _repositionWindow = Random.Range(1.8f, 3.5f);
                        _exposedInCoverTimer = 0f;
                        SquadBlackboard.Get(_ai.squadID)
                            ?.RegisterDestination(_ai, _coverPos);
                    }
                    else
                    { ForceRole(CombatRole.Advance); return; }
                }

                _ai.CombatMoveTo(_coverPos, SpeedMultiplier);
                if (Vector3.Distance(_ai.transform.position, _coverPos) < 1f)
                {
                    _atCover = true;
                    IsInCover = true;
                    _ai.CombatStop();
                }
            }
            else
            {
                IsInCover = true;
                Vector3 target = GetBestKnownPos();
                _ai.CombatFaceToward(target, 150f);

                if (_threat != null && _threat.HasLOS)
                {
                    ShootAt(target);
                    _exposedInCoverTimer += Time.deltaTime;
                }
                else
                {
                    _exposedInCoverTimer = 0f;
                }

                bool exposedTooLong = _exposedInCoverTimer >= _repositionWindow;
                bool lostLOS = _threat == null
                                   || (!_threat.HasLOS && _threat.TimeSinceSeen > 2.5f);

                if (exposedTooLong || lostLOS)
                {
                    IsInCover = false;
                    ForceRole((_threat != null && _threat.HasLOS)
                        ? CombatRole.Suppress : CombatRole.Reposition);
                }
            }
        }
        // --- Reposition ------------------------------------------------------
        // Move to a new angle on threat to gain LOS without closing distance.
        // Orbits around LastKnownPosition at current range.

        private void TickReposition()
        {
            if (!_roleDestSet)
            {
                Vector3 threatPos = GetRoutingDest();
                Vector3 toUnit = (_ai.transform.position - threatPos);
                toUnit.y = 0f;
                float range = Mathf.Clamp(toUnit.magnitude, 8f, 25f);
                float currentAngle = Mathf.Atan2(toUnit.x, toUnit.z) * Mathf.Rad2Deg;
                float[] offsets = { 45f, -45f, 70f, -70f, 100f, -100f, 130f, -130f };

                Vector3 bestPos = Vector3.zero;
                float bestScore = float.MinValue;
                bool found = false;

                foreach (float offset in offsets)
                {
                    float newAngle = (currentAngle + offset) * Mathf.Deg2Rad;
                    Vector3 newDir = new Vector3(Mathf.Sin(newAngle), 0f,
                                                   Mathf.Cos(newAngle));
                    Vector3 tryPos = threatPos + newDir * range;

                    if (!NavMesh.SamplePosition(tryPos, out var hit, 4f,
                        NavMesh.AllAreas)) continue;
                    if (!NavRouter.HasPath(_ai.transform.position, hit.position))
                        continue;
                    if (Vector3.Distance(hit.position, _ai.transform.position) < 3f)
                        continue;

                    // Score -- prefer cover but dont require it
                    float score = 0f;
                    bool hasCover = TacticalFilter.IsExposedToThreat(
                        hit.position, threatPos) == false;
                    if (hasCover) score += 10f;

                    // Prefer positions that are closer to us (less travel)
                    float travelDist = Vector3.Distance(
                        _ai.transform.position, hit.position);
                    score -= travelDist * 0.1f;

                    // Prefer angles that are more different from current
                    score += Mathf.Abs(offset) * 0.05f;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPos = hit.position;
                        found = true;
                    }
                }

                if (!found) { ForceRole(CombatRole.Suppress); return; }

                _roleDestination = bestPos;
                _waypoints = TacticalPathfinder.BuildAdvanceRoute(
                    _ai, _roleDestination);
                _waypointIdx = 0;
                _roleDestSet = true;
            }

            if (_waypoints != null && _waypoints.Count > 0)
            {
                if (_waypointIdx < _waypoints.Count)
                    _ai.CombatMoveTo(_waypoints[_waypointIdx], SpeedMultiplier);
                bool done = TacticalPathfinder.FollowWaypoints(_ai, _waypoints,
                    ref _waypointIdx);
                if (done)
                {
                    _ai.CombatStop();
                    _ai.CombatFaceToward(GetRoutingDest(), 120f);
                    if (_threat.HasLOS)
                        ForceRole(CombatRole.Suppress);
                    else if (_roleTimer > _roleMaxTime * 0.8f)
                        ForceRole(CombatRole.Cautious);
                }
            }
            else
                _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);
        }

        // --- Cautious --------------------------------------------------------

        private void TickCautious()
        {
            if (_cautiousWaiting)
            {
                _cautiousWaitTimer += Time.deltaTime;
                _ai.CombatStop();
                _ai.CombatFaceToward(GetBestKnownPos(), 80f);
                if (_threat.HasLOS) { ForceRole(CombatRole.Cover); return; }
                if (_cautiousWaitTimer > Random.Range(0.8f, 1.5f))
                {
                    _cautiousWaiting = false;
                    _cautiousWaitTimer = 0f;
                    _roleDestSet = false;
                }
                return;
            }

            if (!_roleDestSet)
            {
                Vector3 dest = GetRoutingDest();
                Vector3 toward = (dest - _ai.transform.position).normalized;
                Vector3 next = _ai.transform.position + toward * 5f;
                _roleDestination = NavMesh.SamplePosition(next, out var hit, 3f,
                    NavMesh.AllAreas) ? hit.position : dest;
                _roleDestSet = true;
            }

            _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);
            if (Vector3.Distance(_ai.transform.position, _roleDestination) < 1f)
            {
                _cautiousWaiting = true;
                _cautiousWaitTimer = 0f;
            }

            if (Vector3.Distance(_ai.transform.position, GetRoutingDest()) < 6f)
                ForceRole(CombatRole.Cover);
        }


        // ---------- Constants ------------------------------------------------
        private const float CohesionRadius = 18f;  // POINT 5: max spread from squad
        private const float BlackboardRadius = 10f;  // POINT 2: search overlap prevention

        // --- Search ----------------------------------------------------------
        // POINT 2: registers search dest on blackboard to prevent overlap
        // POINT 5: checks cohesion -- stays within CohesionRadius of squad center

        private float _searchScanTimer;

        private void TickSearch()
        {
            var board = SquadBlackboard.Get(_ai.squadID);

            if (!_roleDestSet)
            {
                // Get sector -- if Tactician hasnt assigned one yet,
                // use squad index spread so guards dont all go north
                float myAngle;
                bool hasSector = _brain?.Tactician != null
                    && _brain.Tactician.HasSearchSector(_ai);
                if (hasSector)
                    myAngle = _brain.Tactician.GetSearchSectorAngle(_ai);
                else
                    myAngle = GetSquadIndex() * (360f / Mathf.Max(1f, GetSquadCount()))
                           + UnityEngine.Random.Range(-20f, 20f);

                float rad = myAngle * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));

                // POINT 5: clamp search dest within cohesion radius of squad center
                Vector3 squadCenter = (_brain != null && _brain.SquadAnchor != Vector3.zero)
                    ? _brain.SquadAnchor : GetSquadCenter();
                float maxDist = _brain?.CoherencyRadius ?? CohesionRadius;
                float searchDist = Random.Range(8f, 14f);
                Vector3 tryPos = _ai.transform.position + dir * searchDist;

                if (Vector3.Distance(tryPos, squadCenter) > maxDist)
                    tryPos = squadCenter + (tryPos - squadCenter).normalized * maxDist * 0.8f;

                // POINT 2: avoid positions already being searched
                if (board != null && board.IsPointCovered(tryPos, BlackboardRadius))
                {
                    // Rotate 45 degrees to find uncovered sector
                    float altRad = (myAngle + 45f) * Mathf.Deg2Rad;
                    Vector3 altDir = new Vector3(Mathf.Sin(altRad), 0f, Mathf.Cos(altRad));
                    tryPos = _ai.transform.position + altDir * searchDist;
                }

                if (NavMesh.SamplePosition(tryPos, out var hit, 5f, NavMesh.AllAreas))
                    _roleDestination = hit.position;
                else
                    _roleDestination = _ai.transform.position + dir * 5f;

                // POINT 2: register on blackboard
                board?.RegisterSearchUnit(_ai, _roleDestination);

                // Direct path -- dest is not threat, cover routing would block all waypoints
                _waypoints = BuildDirectPath(_roleDestination);
                _waypointIdx = 0;
                _roleDestSet = true;
                _searchScanTimer = 0f;
            }

            float distToDest = Vector3.Distance(_ai.transform.position, _roleDestination);
            if (distToDest > 2f)
            {
                Vector3 moveTarget = (_waypoints != null && _waypointIdx < _waypoints.Count)
                    ? _waypoints[_waypointIdx] : _roleDestination;
                _ai.CombatMoveTo(moveTarget, SpeedMultiplier * 0.8f);
                if (_waypoints != null)
                    TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx);
            }
            else
            {
                _ai.CombatStop();
                _searchScanTimer += Time.deltaTime;
                _ai.CombatFaceToward(GetRoutingDest(), 60f);
                if (_searchScanTimer > 2f)
                {
                    board?.UnregisterSearchUnit(_ai); // POINT 2: free slot
                    _roleDestSet = false;
                }
            }
        }

        // --- Overwatch -------------------------------------------------------

        private void TickOverwatch()
        {
            if (!_roleDestSet)
            {
                var ep = _brain.CQB.ActiveEntry;
                Vector3 tryPos = ep != null
                    ? ep.transform.position + (-ep.transform.forward) * 4f
                    : _ai.transform.position;
                _roleDestination = NavMesh.SamplePosition(tryPos, out var hit, 4f,
                    NavMesh.AllAreas) ? hit.position : _ai.transform.position;
                _roleDestSet = true;
            }
            float dist = Vector3.Distance(_ai.transform.position, _roleDestination);
            if (dist > 1.5f)
                _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);
            else
            {
                _ai.CombatStop();
                var ep = _brain.CQB.ActiveEntry;
                _ai.CombatFaceToward(
                    ep != null ? ep.transform.position : GetRoutingDest(), 80f);
                if (_threat != null && _threat.HasLOS)
                    ShootAt(_threat.EstimatedPosition);
            }
        }

        // --- Rear Security ---------------------------------------------------
        // Navigate around building to alternate entry point

        private void TickRearSecurity()
        {
            if (!_roleDestSet)
            {
                Vector3 threatPos = GetRoutingDest();
                var allEps = EntryPointRegistry.FindAllNear(threatPos, 20f);
                var primary = _brain.CQB.ActiveEntry;
                EntryPoint rearTarget = null;
                for (int i = 0; i < allEps.Count; i++)
                    if (allEps[i] != primary) { rearTarget = allEps[i]; break; }

                if (rearTarget != null)
                {
                    _roleDestination = rearTarget.StackLeftPos;
                    _waypoints = BuildDirectPath(_roleDestination);
                }
                else { ForceRole(CombatRole.Reposition); return; }

                _waypointIdx = 0;
                _roleDestSet = true;
            }

            float dist = Vector3.Distance(_ai.transform.position, _roleDestination);
            if (dist > 1.5f)
            {
                Vector3 moveTarget = (_waypoints != null && _waypointIdx < _waypoints.Count)
                    ? _waypoints[_waypointIdx] : _roleDestination;
                _ai.CombatMoveTo(moveTarget, SpeedMultiplier);
                if (_waypoints != null)
                    TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx);
            }
            else
            {
                _ai.CombatStop();
                _ai.CombatFaceToward(GetRoutingDest(), 80f);
                if (_threat != null && _threat.HasLOS) ShootAt(_threat.EstimatedPosition);
            }
        }

        // --- Withdraw --------------------------------------------------------

        private void TickWithdraw()
        {
            if (!_roleDestSet)
            {
                var route = TacticalPathfinder.BuildWithdrawRoute(_ai, GetRoutingDest());
                _waypoints = route;
                _roleDestination = (route != null && route.Count > 0)
                    ? route[route.Count - 1]
                    : _ai.transform.position - _ai.transform.forward * 8f;
                _waypointIdx = 0;
                _roleDestSet = true;
            }
            Vector3 wTarget = (_waypoints != null && _waypointIdx < _waypoints.Count)
                ? _waypoints[_waypointIdx] : _roleDestination;
            _ai.CombatMoveTo(wTarget, SpeedMultiplier * 1.3f);
            if (_waypoints != null)
                TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx);
        }


        // ---------- Squad cohesion helper ------------------------------------
        // POINT 5: returns average position of live squad members

        private Vector3 GetSquadCenter()
        {
            var units = HuntDirector.AllUnits;
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].IsDead) continue;
                if (units[i].squadID != _ai.squadID) continue;
                sum += units[i].transform.position;
                n++;
            }
            return n > 0 ? sum / n : _ai.transform.position;
        }

        // --- CQB -------------------------------------------------------------

        private void TickCQB()
        {
            if (!_brain.CQB.IsActive) { ForceRole(CombatRole.Advance); return; }

            if (_cqbAction == null)
            {
                var role = _brain.CQB.GetRole(_ai);
                // All guards stack first -- holders hold fatal funnel then follow
                _cqbAction = (role.HasValue && role.Value.IsHolder)
                    ? (GoapAction)new HoldFatalFunnelAction()
                    : new StackAction();
                _cqbAction.OnEnter(_ai, _threat);
            }

            bool done = _cqbAction.Execute(_ai, _threat, _brain, Time.deltaTime);
            if (!done) return;

            _cqbAction.OnExit(_ai);

            // All guards progress: Stack/Hold -> Breach -> ClearCorner
            GoapAction next = _cqbAction switch
            {
                StackAction => new BreachAction(),
                HoldFatalFunnelAction => new BreachAction(), // holder follows as support
                BreachAction => new ClearCornerAction(),
                _ => null,
            };

            if (next != null) { _cqbAction = next; _cqbAction.OnEnter(_ai, _threat); }
            else { _cqbAction = null; ForceRole(CombatRole.Advance); }
        }

        // ---------- CQB evaluation -------------------------------------------

        private void TickCQBEvaluation()
        {
            if (_brain.CQB.IsActive || !_threat.HasIntel) return;

            Vector3 threatPos = _threat.EstimatedPosition;

            // Only evaluate CQB if an entry point is the natural route to player.
            // Check: is there a direct NavMesh path to player WITHOUT going through
            // an entry point? If yes, normal combat -- no CQB needed.
            var ep = EntryPointRegistry.FindBest(_ai.transform.position, threatPos);
            if (ep == null) return; // no entry points near threat

            float distEpToThreat = Vector3.Distance(ep.transform.position, threatPos);
            if (distEpToThreat > 12f) return; // entry point not close to threat

            // Check if direct path to threat is shorter than path via entry point
            // If guard can reach threat directly without entry point, skip CQB
            float directDist = NavRouter.PathLength(_ai.transform.position, threatPos);
            float viaEntryDist = NavRouter.PathLength(_ai.transform.position,
                                      ep.transform.position)
                                + distEpToThreat;

            // CQB only if going via entry point is necessary (no shorter direct path)
            // Allow 20% tolerance -- entry point must be clearly the required route
            if (directDist > 0f && directDist < viaEntryDist * 1.2f) return;

            var members = new List<StealthHuntAI>();
            var all = HuntDirector.AllUnits;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == _ai.squadID && !all[i].IsDead)
                    members.Add(all[i]);

            members.Sort((a, b) =>
                Vector3.Distance(a.transform.position, threatPos)
                .CompareTo(
                Vector3.Distance(b.transform.position, threatPos)));

            bool started = _brain.CQB.EvaluateEntry(_ai.transform.position,
                threatPos, _threat.Confidence, members);

            if (started)
            {
                _brain.SetCommittedGoal(TacticalBrain.CommittedGoalData.GoalType.ClearRoom,
                    threatPos, 120f);
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] == null || all[i].squadID != _ai.squadID) continue;
                    all[i].GetComponent<StandardCombat>()?.ForceRole(CombatRole.CQB);
                }
            }
        }


        // ---------- Threat update --------------------------------------------

        private void UpdateThreat()
        {
            var target = _ai.GetTarget();
            if (target == null || _brain == null) return;
            bool canSee = _ai.Sensor != null && _ai.Sensor.CanSeeTarget;
            if (canSee)
                _brain.Intel.Threat.UpdateWithSight(target.Position, target.Velocity);
            else
                _brain.Intel.UpdateNoSight();
        }

        // ---------- Event processing -----------------------------------------

        private void ProcessEvents()
        {
            if (_events == null || !_events.HasEvents) return;
            if (_brain == null || _threat == null) return;
            var evt = _events.ConsumeHighestPriority();
            if (evt == null) return;
            switch (evt.Value.Type)
            {
                case CombatEventType.DamageTaken:
                case CombatEventType.Ambushed:
                    if (evt.Value.Position != Vector3.zero)
                        _threat.RegisterShotFrom(evt.Value.Position,
                            (evt.Value.Position - _ai.transform.position).normalized);
                    // Interrupt current role -- re-claim after recovery
                    ForceRole(_threat.HasLOS ? CombatRole.Cover : CombatRole.Reposition);
                    _lastScenario = (SquadTactician.TacticianScenario)(-1);
                    break;
                case CombatEventType.BuddyDown:
                    // Squadmate killed -- suppress and reposition
                    if (_role != CombatRole.Withdraw)
                        ForceRole(_threat.HasLOS ? CombatRole.Suppress : CombatRole.Reposition);
                    break;
                case CombatEventType.BuddyNeedsHelp:
                    if (_threat.HasLOS) ForceRole(CombatRole.Suppress);
                    break;
                case CombatEventType.ThreatFlank:
                    _brain.Intel.Threat.ReceiveIntel(evt.Value.Position, Vector3.zero, 0.8f);
                    if (_brain.CommittedGoal != null &&
                        Vector3.Distance(evt.Value.Position,
                            _brain.CommittedGoal.Position) > 15f)
                        _brain.InterruptCommittedGoal("new intel");
                    ForceRole(CombatRole.Reposition);
                    break;
                case CombatEventType.ThreatFound:
                    if (_role == CombatRole.Search || _role == CombatRole.Idle)
                        ForceRole(CombatRole.Cautious);
                    break;
                case CombatEventType.ThreatLost:
                    if (_role == CombatRole.Advance || _role == CombatRole.Suppress)
                        ForceRole(CombatRole.Cautious);
                    break;
            }
        }
        // ---------- Helpers --------------------------------------------------

        private void ShootAt(Vector3 pos)
        {
            Vector3 origin = _ai.transform.position + Vector3.up * 1.5f;
            Vector3 toT = pos + Vector3.up * 0.8f - origin;
            if (Physics.Raycast(origin, toT.normalized, toT.magnitude - 0.3f,
                LayerMask.GetMask("Default", "Environment"))) return;
            _ai.GetComponent<IShootable>()?.TryShoot(pos);
        }


        // --- Direct path (no cover routing) --------------------------------
        // Used when destination is NOT a threat position.
        // BuildAdvanceRoute uses dest as threatPos which incorrectly filters waypoints.
        private List<Vector3> BuildDirectPath(Vector3 dest)
        {
            var path = new UnityEngine.AI.NavMeshPath();
            if (!NavMesh.CalculatePath(_ai.transform.position, dest,
                NavMesh.AllAreas, path)) return null;
            if (path.status == UnityEngine.AI.NavMeshPathStatus.PathInvalid) return null;
            var result = new List<Vector3>();
            for (int i = 1; i < path.corners.Length; i++)
                result.Add(path.corners[i]);
            return result;
        }


        // --- TacticalRole mapping -------------------------------------------
        private static CombatRole FromTactical(SquadBlackboard.TacticalRole r) => r switch
        {
            SquadBlackboard.TacticalRole.Advance => CombatRole.Advance,
            SquadBlackboard.TacticalRole.Flank => CombatRole.Flank,
            SquadBlackboard.TacticalRole.Suppress => CombatRole.Suppress,
            SquadBlackboard.TacticalRole.Cover => CombatRole.Cover,
            SquadBlackboard.TacticalRole.Cautious => CombatRole.Cautious,
            SquadBlackboard.TacticalRole.Reposition => CombatRole.Reposition,
            SquadBlackboard.TacticalRole.Search => CombatRole.Search,
            SquadBlackboard.TacticalRole.Overwatch => CombatRole.Overwatch,
            SquadBlackboard.TacticalRole.RearSecurity => CombatRole.RearSecurity,
            SquadBlackboard.TacticalRole.Breach => CombatRole.Breach,
            SquadBlackboard.TacticalRole.Follow => CombatRole.Follow,
            SquadBlackboard.TacticalRole.Withdraw => CombatRole.Withdraw,
            SquadBlackboard.TacticalRole.CQB => CombatRole.CQB,
            _ => CombatRole.Idle,
        };

        private Vector3 GetRoutingDest()
            => _threat.LastSeenTime > -999f
                ? _threat.LastKnownPosition
                : _threat.EstimatedPosition;

        private Vector3 GetBestKnownPos()
        {
            if (_threat.HasLOS) return _threat.EstimatedPosition;
            if (_threat.LastSeenTime > -999f) return _threat.LastKnownPosition;
            var board = SquadBlackboard.Get(_ai.squadID);
            if (board != null && board.SharedConfidence > 0.1f) return board.SharedLastKnown;
            return _ai.transform.position + _ai.transform.forward * 10f;
        }

        private TacticalSpot FindCoverSpot()
        {
            if (TacticalSystem.Instance == null) return null;
            var ctx = TacticalContext.Build(_ai, _threat, _brain, 20f);
            return TacticalSystem.Instance.EvaluateSync(ctx);
        }

        private void ReleaseCoverSpot()
        {
            IsInCover = false;
            _reservedSpot = null;
        }

        private void YieldToCore()
        {
            WantsControl = false;
            ReleaseCoverSpot();
            _ai?.CombatRestoreRotation();
            // Keep brain alive -- fast re-entry on next OnEnterCombat
        }

        private int CountSquadRole(CombatRole role)
        {
            int count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID
                 || units[i] == _ai) continue;
                var sc = units[i].GetComponent<StandardCombat>();
                if (sc != null && sc._role == role) count++;
            }
            return count;
        }

        private bool IsSquadLeader()
        {
            int min = int.MaxValue;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID
                 || units[i].IsDead) continue;
                int id = units[i].GetInstanceID();
                if (id < min) min = id;
            }
            return _ai.GetInstanceID() == min;
        }

        private int GetSquadIndex()
        {
            int idx = 0, count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null || units[i].squadID != _ai.squadID) continue;
                if (units[i] == _ai) idx = count;
                count++;
            }
            return idx;
        }

        private int GetSquadCount()
        {
            int count = 0;
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
                if (units[i] != null && units[i].squadID == _ai.squadID) count++;
            return count;
        }

        private void CheckStuck()
        {
            var agent = _ai?.GetComponent<NavMeshAgent>();
            if (agent == null || agent.isStopped || !agent.hasPath) return;
            if (agent.pathStatus == NavMeshPathStatus.PathPartial
             || agent.pathStatus == NavMeshPathStatus.PathInvalid)
            { _roleDestSet = false; _waypoints = null; return; }
            if (agent.desiredVelocity.magnitude < 0.1f) return;
            float moved = Vector3.Distance(_ai.transform.position, _lastPos);
            if (moved > 0.5f) { _stuckTimer = 0f; _lastPos = _ai.transform.position; return; }
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer > 2f)
            {
                _blockedCells.Add(WorldToCell(agent.destination));
                _roleDestSet = false;
                _waypoints = null;
                _roleTimer = _roleMaxTime; // force role reselect with new dest
                _stuckTimer = 0f; _lastPos = _ai.transform.position;
                agent.ResetPath();
            }
            _blockedTimer += Time.deltaTime;
            if (_blockedTimer > 8f) { _blockedCells.Clear(); _blockedTimer = 0f; }
        }

        private static Vector3Int WorldToCell(Vector3 p)
            => new Vector3Int(Mathf.RoundToInt(p.x / 2f),
                              Mathf.RoundToInt(p.y / 2f),
                              Mathf.RoundToInt(p.z / 2f));

        public bool IsDestinationBlocked(Vector3 dest)
            => _blockedCells.Contains(WorldToCell(dest));
    }
}