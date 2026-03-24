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

        public enum Goal { Idle, AdvanceTo, Flank, Suppress, HoldAndFire, Search }
        public Goal CurrentGoal { get; private set; }
        public bool IsInCover { get; private set; }

        public enum CombatRole { Advance, Flank, Suppress, Cover, Cautious, CQB, Idle }

        private StealthHuntAI _ai;
        private TacticalBrain _brain;
        private CombatEventBus _events;
        private ThreatModel _threat;

        internal CombatRole _role = CombatRole.Idle; // internal for squad coordination
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
            _brain = TacticalBrain.GetOrCreate(ai.squadID);
            _brain.RegisterMember(ai);
            _events = CombatEventBus.Get(ai);
            _threat = new ThreatModel();
            _threat.OnEnterCombat();

            var board = SquadBlackboard.Get(ai.squadID);
            if (board != null && board.SharedConfidence > 0.05f)
                _threat.ReceiveIntel(board.SharedLastKnown, Vector3.zero,
                                     board.SharedConfidence);

            var target = ai.GetTarget();
            if (target != null && ai.Sensor.CanSeeTarget)
                _threat.UpdateWithSight(target.Position, target.Velocity);

            SelectRole();
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
                         || _role == CombatRole.Cautious;
            if (!isMoving) ForceRole(CombatRole.Cover);
        }

        public void Tick(StealthHuntAI ai)
        {
            _ai = ai;
            UpdateThreat();
            ProcessEvents();
            CheckStuck();

            if (IsSquadLeader())
            {
                var ws = WorldState.Build(_ai, _threat, _brain);
                _brain.Strategy.Update(Time.deltaTime, ws, _brain);
                _brain.TickCommittedGoal();
                _brain.CQB.Tick(Time.deltaTime);
                TickCQBEvaluation();
            }
            else
            {
                _brain.CQB.Tick(Time.deltaTime);
            }

            if (_brain.CQB.IsActive && _brain.CQB.RoomCleared)
            {
                _brain.CQB.EndEntry();
                _brain.ClearCommittedGoal();
                if (_threat.HasIntel && !_threat.HasLOS)
                    ForceRole(CombatRole.Cautious);
            }

            TickCombatLoop();

            bool stale = _threat.Confidence < 0.1f || _threat.TimeSinceSeen > 30f;
            if (stale) { YieldToCore(); return; }

            _roleTimer += Time.deltaTime;
            if (_roleTimer >= _roleMaxTime || _role == CombatRole.Idle)
                SelectRole();

            TickRole();

            HuntDirector.RegisterHeat(ai.transform.position, 0.04f * Time.deltaTime);
        }

        // ---------- Combat loop ----------------------------------------------

        private void TickCombatLoop()
        {
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
            if (_brain.CQB.IsActive) { SetRole(CombatRole.CQB); return; }

            var ws = WorldState.Build(_ai, _threat, _brain);

            if (!ws.HasIntel) { SetRole(CombatRole.Idle); return; }
            if (ws.SquadStrength < 0.25f || ws.Health < 0.2f)
            { SetRole(CombatRole.Cover); return; }

            int idx = GetSquadIndex();
            var strategy = _brain.Strategy.Current;

            // Count existing roles so we dont stack same role
            int advancing = CountSquadRole(CombatRole.Advance);
            int suppressing = CountSquadRole(CombatRole.Suppress);
            int flanking = CountSquadRole(CombatRole.Flank);
            int squadSize = GetSquadCount();

            CombatRole role = strategy switch
            {
                SquadStrategy.Bounding => advancing < squadSize / 2 + 1
                                            ? CombatRole.Advance : CombatRole.Suppress,
                SquadStrategy.Pincer => flanking < 2
                                            ? CombatRole.Flank
                                            : suppressing == 0 ? CombatRole.Suppress
                                            : CombatRole.Advance,
                SquadStrategy.Suppress => suppressing < squadSize * 0.6f
                                            ? CombatRole.Suppress : CombatRole.Advance,
                SquadStrategy.Overwatch => idx % 2 == 0 ? CombatRole.Advance : CombatRole.Cover,
                SquadStrategy.Withdraw => CombatRole.Cover,
                _ => advancing < Mathf.Max(1, squadSize / 2)
                                            ? CombatRole.Advance : CombatRole.Suppress,
            };

            // Use cautious only when actively being shot at from unknown direction
            if (_threat.HasShotFrom && !_threat.HasLOS)
                if (role == CombatRole.Advance) role = CombatRole.Cautious;

            SetRole(role);
        }

        public void ForceRole(CombatRole role)
        {
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
                CombatRole.Cover => 3f,  // shorter cover -- keeps guards moving
                CombatRole.Advance => 12f, // longer advance -- commit to push
                CombatRole.Flank => 15f, // flanks take time
                CombatRole.Cautious => 10f,
                CombatRole.CQB => 30f,
                _ => 8f,
            };
            CurrentGoal = role switch
            {
                CombatRole.Advance => Goal.AdvanceTo,
                CombatRole.Flank => Goal.Flank,
                CombatRole.Suppress => Goal.Suppress,
                CombatRole.Cover => Goal.HoldAndFire,
                CombatRole.Cautious => Goal.AdvanceTo,
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
                case CombatRole.CQB: TickCQB(); break;
                case CombatRole.Idle: if (_threat.HasIntel) SelectRole(); break;
            }
        }

        // --- Advance ---------------------------------------------------------

        private void TickAdvance()
        {
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
                _roleDestination = dest + perp * spread;
                _waypoints = TacticalPathfinder.BuildAdvanceRoute(_ai, _roleDestination);
                _waypointIdx = 0;
                _roleDestSet = true;
            }

            if (_waypoints != null && _waypoints.Count > 0)
                TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx,
                    SpeedMultiplier);
            else
                _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);

            if (Vector3.Distance(_ai.transform.position, _roleDestination) < 4f)
            {
                // Arrived -- hold and fire briefly, then planner picks next role
                _roleTimer = _roleMaxTime; // trigger role reselect
            }
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
            }

            if (_waypoints != null && _waypoints.Count > 0)
                TacticalPathfinder.FollowWaypoints(_ai, _waypoints, ref _waypointIdx,
                    SpeedMultiplier);
            else
                _ai.CombatMoveTo(_roleDestination, SpeedMultiplier);

            if (Vector3.Distance(_ai.transform.position, _roleDestination) < 2f)
                ForceRole(CombatRole.Cover);
        }

        // --- Suppress --------------------------------------------------------

        private void TickSuppress()
        {
            _ai.CombatStop();
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
                _suppressBurstTimer += Time.deltaTime;
                if (_suppressBurstTimer > 0.35f)
                {
                    _suppressBurstTimer = 0f;
                    ShootAt(target);
                    if (++_suppressBurst >= 3)
                    {
                        _suppressBurst = 0;
                        ForceRole(CombatRole.Advance);
                    }
                }
            }
        }

        // --- Cover -----------------------------------------------------------

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
                    _peekTimer = 0f;
                }
            }
            else
            {
                IsInCover = true;
                _peekTimer += Time.deltaTime;
                Vector3 target = GetBestKnownPos();
                _ai.CombatFaceToward(target, 150f);
                if (_threat.HasLOS) { ShootAt(target); _peekTimer = 0f; }
                if (_peekTimer > 1.5f) { IsInCover = false; ForceRole(CombatRole.Advance); }
            }
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

        // --- CQB -------------------------------------------------------------

        private void TickCQB()
        {
            if (!_brain.CQB.IsActive) { ForceRole(CombatRole.Advance); return; }

            if (_cqbAction == null)
            {
                var role = _brain.CQB.GetRole(_ai);
                _cqbAction = (!role.HasValue || role.Value.IsHolder)
                    ? (GoapAction)new HoldFatalFunnelAction()
                    : new StackAction();
                _cqbAction.OnEnter(_ai, _threat);
            }

            bool done = _cqbAction.Execute(_ai, _threat, _brain, Time.deltaTime);
            if (!done) return;

            _cqbAction.OnExit(_ai);
            var r = _brain.CQB.GetRole(_ai);
            if (r.HasValue && !r.Value.IsHolder)
            {
                GoapAction next = _cqbAction switch
                {
                    StackAction => new BreachAction(),
                    BreachAction => new ClearCornerAction(),
                    _ => null,
                };
                if (next != null) { _cqbAction = next; _cqbAction.OnEnter(_ai, _threat); }
                else { _cqbAction = null; ForceRole(CombatRole.Advance); }
            }
            else
            { _cqbAction = null; ForceRole(CombatRole.Advance); }
        }

        // ---------- CQB evaluation -------------------------------------------

        private void TickCQBEvaluation()
        {
            if (_brain.CQB.IsActive || !_threat.HasIntel) return;

            var members = new List<StealthHuntAI>();
            var all = HuntDirector.AllUnits;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].squadID == _ai.squadID && !all[i].IsDead)
                    members.Add(all[i]);

            members.Sort((a, b) =>
                Vector3.Distance(a.transform.position, _threat.EstimatedPosition)
                .CompareTo(
                Vector3.Distance(b.transform.position, _threat.EstimatedPosition)));

            bool started = _brain.CQB.EvaluateEntry(_ai.transform.position,
                _threat.EstimatedPosition, _threat.Confidence, members);

            if (started)
            {
                _brain.SetCommittedGoal(TacticalBrain.CommittedGoalData.GoalType.ClearRoom,
                    _threat.EstimatedPosition, 120f);
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
            if (target == null) return;
            if (_ai.Sensor.CanSeeTarget)
            {
                _threat.UpdateWithSight(target.Position, target.Velocity);
                _brain.ReportThreat(_ai, target.Position, target.Velocity, 1f);
            }
            else
            {
                _threat.UpdateWithoutSight();
                if (_brain.SharedThreat.Confidence > _threat.Confidence + 0.2f)
                    _threat.ReceiveIntel(_brain.SharedThreat.EstimatedPosition,
                        _brain.SharedThreat.LastKnownVelocity,
                        _brain.SharedThreat.Confidence * 0.85f);
                _brain.UpdateNoSight();
            }
        }

        // ---------- Event processing -----------------------------------------

        private void ProcessEvents()
        {
            if (_events == null || !_events.HasEvents) return;
            var evt = _events.ConsumeHighestPriority();
            if (evt == null) return;
            switch (evt.Value.Type)
            {
                case CombatEventType.DamageTaken:
                case CombatEventType.Ambushed:
                    if (evt.Value.Position != Vector3.zero)
                        _threat.RegisterShotFrom(evt.Value.Position,
                            (evt.Value.Position - _ai.transform.position).normalized);
                    ForceRole(_threat.HasLOS ? CombatRole.Cover : CombatRole.Cautious);
                    break;
                case CombatEventType.ThreatFlank:
                    _threat.ReceiveIntel(evt.Value.Position, Vector3.zero, 0.8f);
                    if (_brain.CommittedGoal != null &&
                        Vector3.Distance(evt.Value.Position,
                            _brain.CommittedGoal.Position) > 15f)
                        _brain.InterruptCommittedGoal("new intel");
                    ForceRole(CombatRole.Cover);
                    break;
                case CombatEventType.ThreatFound:
                    if (_role == CombatRole.Idle) SelectRole();
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
            var ctx = new TacticalContext
            {
                Unit = _ai,
                UnitPosition = _ai.transform.position,
                EstimatedThreatPos = _threat.EstimatedPosition,
                Threat = _threat,
                SearchRadius = 20f,
                NavMeshMask = NavMesh.AllAreas,
            };
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
            _brain?.UnregisterMember(_ai);
            ReleaseCoverSpot();
            _ai?.CombatRestoreRotation();
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
                _roleDestSet = false; _waypoints = null;
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