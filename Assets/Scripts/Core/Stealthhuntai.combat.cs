using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    public partial class StealthHuntAI
    {
        // ---------- Shared helpers -------------------------------------------

        /// <summary>
        /// Periodically rotates unit toward a random nearby point while standing still.
        /// Simulates a guard scanning an area -- called during Searching when idle.
        /// </summary>
        private void TickLookAround()
        {
            _lookAroundTimer += Time.deltaTime;

            // Pick a new look target every 1.5-3 seconds
            if (!_hasLookTarget || _lookAroundTimer >= Random.Range(1.5f, 3.0f))
            {
                _lookAroundTimer = 0f;
                _hasLookTarget = true;

                // Pick a random horizontal direction with slight vertical variance
                float angle = Random.Range(-120f, 120f);
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * transform.forward;
                _lookAroundTarget = Quaternion.LookRotation(dir);
            }

            // Smoothly rotate toward look target
            transform.rotation = Quaternion.Slerp(
                transform.rotation, _lookAroundTarget,
                Time.deltaTime * 2.5f);
        }

        // ---------- Suspicious sub-states -------------------------------------

        private void TickAlerted()
        {
            StopMoving();

            // Turn toward the stimulus source -- "what was that?" reaction
            if (_sensor != null && _sensor.StimulusConfidence > 0.05f)
            {
                Vector3 toStimulus = _sensor.LastStimulusPosition - transform.position;
                toStimulus.y = 0f;

                if (toStimulus.magnitude > 0.1f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(toStimulus);
                    transform.rotation = Quaternion.Slerp(
                        transform.rotation, targetRot,
                        Time.deltaTime * 4f);
                }
            }

            if (_stateTimer > 1.2f)
            {
                if (_hasLastKnown)
                    TransitionSubState(SubState.Investigating);
                else
                    TransitionSubState(SubState.Searching);
            }
        }

        private void TickInvestigating()
        {
            if (!_hasLastKnown)
            {
                _searchCenter = transform.position;
                TransitionSubState(SubState.Searching);
                return;
            }

            MoveTo(_lastKnownPosition);

            if (_movement.RemainingDistance < 1.0f)
            {
                // Use uncertainty radius to offset search center from last known position.
                // The older the intel, the further away the real position might be.
                float uncertainty = _sensor != null
                    ? _sensor.PositionUncertaintyRadius
                    : 0f;

                if (uncertainty > 0.5f)
                {
                    Vector3 offset = Random.insideUnitSphere * uncertainty;
                    offset.y = 0f;
                    _searchCenter = _lastKnownPosition + offset;
                }
                else
                {
                    _searchCenter = _lastKnownPosition;
                }

                TransitionSubState(SubState.Searching);
            }
        }

        private void TickSearching()
        {
            _searchTimer += Time.deltaTime;

            float uncertaintyBonus = _sensor != null
                ? _sensor.PositionUncertaintyRadius * 0.5f
                : 0f;

            float radius = (behaviourMode == BehaviourMode.GuardZone)
                ? Mathf.Min(searchRadius, guardZoneRadius)
                : searchRadius + uncertaintyBonus;

            // Initialize strategy on first tick
            if (!_scanRequested)
            {
                _scanRequested = true;
                _searchStartTime = Time.time;
                _visitedCells.Clear();

                float timeLost = _searchStartTime
                               - (_sensor != null && _sensor.StimulusConfidence > 0f
                                  ? _sensor.LastStimulus.Timestamp
                                  : _searchStartTime);

                var ctx = new SearchContext
                {
                    LastKnownPosition = _lastKnownPosition,
                    PrimaryStimulus = _sensor != null
                                           ? _sensor.LastStimulus
                                           : default,
                    StimulusHistory = _sensor != null
                                           ? _sensor.GetStimulusHistory()
                                           : new List<StimulusRecord>(),
                    TimeSinceLostTarget = Mathf.Max(timeLost, 0.5f),
                    EstimatedTargetSpeed = _lastSeenFlightVector.magnitude > 0.1f
                                           ? _target != null
                                             ? _target.Speed
                                             : 4f
                                           : 4f,
                    FlightVector = _lastSeenFlightVector,
                    PredictedFlightDir = HuntDirector.PredictedFlightDir,
                    KnownHideSpots = HuntDirector.GetHideSpotSnapshot(),
                    SearchRadius = radius,
                    SearchPointCount = searchPointCount,
                    HeightRange = searchHeightRange,
                    ObstacleMask = _sensor != null
                                           ? _sensor.sightBlockers
                                           : Physics.DefaultRaycastLayers,
                    VisitedCells = _visitedCells,
                    CellSize = visitedCellSize,
                    UnitPosition = transform.position
                };

                // Record that we are searching this region
                RecordSearchedRegion(_lastKnownPosition);

                // Share context with blackboard for squad coordination
                var blackboard = HuntDirector.GetSquadBlackboard(squadID);
                blackboard?.RegisterSearchUnit(this, _lastKnownPosition);

                _searchStrategy?.Initialize(ctx);
            }

            // Wait for strategy -- look around while waiting
            // Timeout after 2s to prevent guard being stuck looking around forever
            if (_searchStrategy != null && !_searchStrategy.IsReady && _stateTimer < 2f)
            {
                TickLookAround();
                return;
            }

            bool arrived = !_movement.HasPath || _movement.RemainingDistance < 0.6f;

            if (arrived)
            {
                // Notify strategy that we reached the last destination
                if (_hasSearchDest)
                {
                    _searchStrategy?.OnPointReached(_currentSearchDest);
                    _hasSearchDest = false;
                }

                // Get next point from strategy
                Vector3? next = _searchStrategy?.GetNextPoint();

                if (next.HasValue)
                {
                    _currentSearchDest = next.Value;
                    _hasSearchDest = true;
                    MoveTo(next.Value);
                }
                else if (_searchStrategy == null || _searchStrategy.IsExhausted)
                {
                    if (_searchPassCount < 1)
                    {
                        // First pass exhausted -- do one wider re-init then stop
                        _searchPassCount++;
                        _scanRequested = false;

                        // Expand search radius slightly for second pass
                        float expanded = Mathf.Min(searchRadius * 1.4f, searchRadius + 10f);
                        searchRadius = expanded;
                    }
                    else
                    {
                        // Second pass also done -- stand still and look around
                        StopMoving();
                        TickLookAround();
                    }
                }
            }

            float effectiveSearchDur = _sensor != null ? _sensor.searchDur : searchDuration;
            if (_searchTimer >= effectiveSearchDur)
            {
                ModifyMorale(-0.08f);

                _hasLastKnown = false;
                _searchTimer = 0f;
                _scanRequested = false;
                _hasSearchDest = false;
                _searchPassCount = 0;
                _searchStrategy?.Reset();
                _visitedCells.Clear();

                var blackboard = HuntDirector.GetSquadBlackboard(squadID);
                blackboard?.UnregisterSearchUnit(this);

                TransitionTo(AlertState.Passive, SubState.Returning);
            }
        }

        private struct ScoredPoint
        {
            public Vector3 Position;
            public float Score;
            public ScoredPoint(Vector3 pos, float score) { Position = pos; Score = score; }
        }


        // ---------- Hostile sub-states ----------------------------------------

        private void TickPursuing()
        {
            // Suppressed guards stop advancing -- duck and wait
            if (IsSuppressed)
            {
                StopMoving();
                return;
            }

            if (!_hasLastKnown)
            {
                _searchCenter = transform.position;
                TransitionSubState(SubState.Searching);
                return;
            }

            float directDist = _sensor.CanSeeTarget && _target != null
                ? Vector3.Distance(transform.position, _target.Position)
                : Vector3.Distance(transform.position, _lastKnownPosition);

            // Use IWeaponProvider interface -- works with any weapon, not just GuardWeapon
            var weapon = GetComponent<IWeaponProvider>();
            float combatRange = weapon != null ? weapon.ShootRange * 0.75f : -1f;

            bool inCombatRange = combatRange > 0f
                && _sensor.CanSeeTarget
                && directDist <= combatRange;

            if (inCombatRange)
            {
                // Transition to Shooting substate
                TransitionSubState(SubState.Shooting);
                return;
            }
            else
            {
                if (_agent != null) _agent.updateRotation = true;
                ApplySpeedToMovement(_baseAgentSpeed * chaseSpeedMultiplier);
                MoveTo(_lastKnownPosition);
            }

            float navDist = _movement.RemainingDistance;
            bool arrived = (navDist < 1.0f) || (directDist < 2.5f && !_movement.HasPath);

            if (arrived && !_sensor.CanSeeTarget)
            {
                if (_agent != null) _agent.updateRotation = true;
                _searchCenter = _lastKnownPosition;
                _lastSeenPosition = _lastKnownPosition;
                _scanRequested = false;
                TransitionSubState(SubState.Searching);
            }
        }

        private void TickShooting()
        {
            // Stop moving and face player
            StopMoving();
            if (_agent != null) _agent.updateRotation = false;

            if (_target != null && _sensor != null && _sensor.CanSeeTarget)
            {
                Vector3 dir = (_target.Position - transform.position);
                dir.y = 0f;
                if (dir.magnitude > 0.1f)
                {
                    transform.rotation = Quaternion.RotateTowards(
                        transform.rotation,
                        Quaternion.LookRotation(dir.normalized),
                        360f * Time.deltaTime);
                }
            }

            // Check if we should go back to pursuing
            var weapon = GetComponent<IWeaponProvider>();
            float combatRange = weapon != null ? weapon.ShootRange * 0.75f : 15f;

            bool stillInRange = _sensor != null && _sensor.CanSeeTarget
                && _target != null
                && Vector3.Distance(transform.position, _target.Position) <= combatRange;

            if (stillInRange)
            {
                // Fire -- call TryShoot on any IShootable component
                var shootable = GetComponent<IShootable>();
                if (shootable != null && _target != null)
                    shootable.TryShoot(_target.Position);
            }
            else
            {
                if (_agent != null) _agent.updateRotation = true;
                TransitionSubState(SubState.Pursuing);
            }
        }

        private void TickFlanking()
        {
            if (!_movement.HasPath || _movement.RemainingDistance < 0.8f)
                TransitionSubState(SubState.Pursuing);
        }

        private void TickLostTarget()
        {
            float radius = searchRadius * 1.8f;

            if (behaviourMode == BehaviourMode.GuardZone)
                radius = Mathf.Min(radius, guardZoneRadius * 1.2f);

            // Initialize ReachabilitySearch on first tick
            if (!_scanRequested)
            {
                _scanRequested = true;
                _hasSearchDest = false;
                _visitedCells.Clear();

                var ctx = new SearchContext
                {
                    LastKnownPosition = _hasLastKnown
                                           ? _lastKnownPosition
                                           : GetHomePosition(),
                    PrimaryStimulus = _sensor != null
                                           ? _sensor.LastStimulus
                                           : default,
                    StimulusHistory = _sensor != null
                                           ? _sensor.GetStimulusHistory()
                                           : new System.Collections.Generic.List<StimulusRecord>(),
                    TimeSinceLostTarget = _stateTimer,
                    EstimatedTargetSpeed = _target != null ? _target.Speed : 4f,
                    FlightVector = _lastSeenFlightVector,
                    PredictedFlightDir = HuntDirector.PredictedFlightDir,
                    KnownHideSpots = HuntDirector.GetHideSpotSnapshot(),
                    SearchRadius = radius,
                    SearchPointCount = searchPointCount,
                    HeightRange = searchHeightRange,
                    ObstacleMask = _sensor != null
                                           ? _sensor.sightBlockers
                                           : Physics.DefaultRaycastLayers,
                    VisitedCells = _visitedCells,
                    CellSize = visitedCellSize,
                    UnitPosition = transform.position
                };

                _searchStrategy?.Initialize(ctx);
                ApplySpeedToMovement(_baseAgentSpeed * chaseSpeedMultiplier);
            }

            // Wait for strategy -- look around while waiting
            // Timeout after 2s to prevent guard being stuck looking around forever
            if (_searchStrategy != null && !_searchStrategy.IsReady && _stateTimer < 2f)
            {
                TickLookAround();
                return;
            }

            bool arrived = !_movement.HasPath || _movement.RemainingDistance < 0.6f;

            if (arrived)
            {
                if (_hasSearchDest)
                {
                    _searchStrategy?.OnPointReached(_currentSearchDest);
                    _hasSearchDest = false;
                }

                Vector3? next = _searchStrategy?.GetNextPoint();

                if (next.HasValue)
                {
                    _currentSearchDest = next.Value;
                    _hasSearchDest = true;
                    MoveTo(next.Value);
                }
                else
                {
                    StopMoving();
                }
            }

            float effectiveLostDur = _sensor != null ? _sensor.searchDur : searchDuration;
            if (_stateTimer >= effectiveLostDur * 1.5f)
            {
                _hasLastKnown = false;
                _scanRequested = false;
                _hasSearchDest = false;
                _searchStrategy?.Reset();
                _visitedCells.Clear();
                onLostTarget.Invoke();
                TransitionTo(AlertState.Passive, SubState.Returning);
            }
        }

    }
}