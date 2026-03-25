using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace StealthHuntAI
{
    // ---------- Scene alert level enum ---------------------------------------

    public enum SceneAlertLevel
    {
        Normal,     // no contact -- standard behaviour
        Caution,    // one or more units suspicious -- heightened awareness
        Alert       // confirmed hostile contact -- all units activated
    }

    // -------------------------------------------------------------------------

    /// <summary>
    /// Scene singleton. Place on one empty GameObject named "HuntSystem".
    /// Manages target registration, squad grouping, tension, and the Director layer
    /// (Alien Isolation style -- knows player position but nudges AI indirectly).
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Hunt Director")]
    [DisallowMultipleComponent]
    public class HuntDirector : MonoBehaviour
    {
        // ---------- Singleton -------------------------------------------------

        private static HuntDirector _instance;

        // Clear statics on every domain reload (play mode enter/exit in editor)
        // Prevents ghost units from persisting between play sessions in editor
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ClearStatics()
        {
            _instance = null;
            _target = null;
            AlertLevel = SceneAlertLevel.Normal;
            _units = new System.Collections.Generic.List<StealthHuntAI>();
            _squads = new System.Collections.Generic.List<SquadBlackboard>();
            _nudgeCooldowns.Clear();
            EvasionTime = 0f;
            _heatMap.Clear();
            _coverPoints.Clear();
            _sectorWatchers.Clear();
            _flightHistory.Clear();
            _knownHideSpots.Clear();
            FlightMemory.Reset();
            System.Array.Clear(_markov, 0, _markov.Length);
            _heatMap.Clear();
            _coverPoints.Clear();
            _sectorWatchers.Clear();
        }

        // ---------- Inspector -------------------------------------------------

        [Header("Tension System")]
        [Tooltip("How fast tension rises.")]
        [Range(0.01f, 1f)] public float tensionRiseSpeed = 0.15f;

        [Tooltip("How fast tension falls when conditions are calm.")]
        [Range(0.01f, 1f)] public float tensionDecaySpeed = 0.08f;

        [Tooltip("Distance at which proximity factor is calculated.")]
        [Range(5f, 50f)] public float tensionZoneRadius = 20f;

        [Header("Tension Mode")]
        [Tooltip("Weight of proximity-based tension (units close to player). " +
                 "0 = disabled, 1 = full weight. Set both weights to taste.")]
        [Range(0f, 1f)] public float proximityWeight = 0.3f;

        [Tooltip("Weight of evasion-time tension (player undetected over time). " +
                 "0 = disabled, 1 = full weight.")]
        [Range(0f, 1f)] public float evasionWeight = 0.7f;

        [Tooltip("Seconds of undetected evasion before evasion factor reaches max. " +
                 "60 = tension maxes out after 1 minute without detection.")]
        [Range(10f, 300f)] public float evasionTimeToMaxTension = 60f;

        [Header("Director Nudges")]
        [Tooltip("Enable Director nudging -- sends passive units toward patrol regions when tension is high. " +
                 "Disable for small levels where natural patrol routes suffice.")]
        public bool enableNudging = false;

        [Tooltip("Tension level at which Director starts steering AI toward player zone.")]
        [Range(0f, 1f)] public float nudgeThreshold = 0.85f;

        [Tooltip("How close Director will route AI to player. Never uses exact position.")]
        [Range(3f, 20f)] public float nudgeOffset = 8f;

        [Tooltip("Seconds between each Director nudge attempt. " +
                 "Lower = more aggressive steering. Higher = more passive.")]
        [Range(5f, 120f)] public float nudgeInterval = 25f;

        [Tooltip("Seconds a unit must wait before it can be nudged again.")]
        [Range(10f, 300f)] public float nudgeCooldown = 60f;

        [Tooltip("How many auto-generated PatrolRegions to create if none exist in scene.")]
        [Range(4, 24)] public int autoRegionCount = 8;

        [Tooltip("Radius of auto-generated regions.")]
        [Range(3f, 20f)] public float autoRegionRadius = 8f;

        [Tooltip("Max distance from player last known to consider a region for nudging.")]
        [Range(10f, 80f)] public float regionSearchRange = 40f;

        [Header("Squad Intel")]
        [Tooltip("Time in seconds before a squad report loses all confidence.")]
        [Range(5f, 60f)] public float intelDecayTime = 15f;

        [Tooltip("Reports below this confidence are ignored by receiving units.")]
        [Range(0f, 1f)] public float minIntelConfidence = 0.2f;

        [Header("Global Alert")]
        [Tooltip("Seconds all units must stay below Hostile before Alert drops to Caution.")]
        [Range(5f, 120f)] public float alertCooldownTime = 30f;

        [Tooltip("Seconds all units must stay Passive before Caution drops to Normal.")]
        [Range(5f, 120f)] public float cautionCooldownTime = 20f;

        [Tooltip("Awareness rise speed multiplier applied to all units during Caution.")]
        [Range(1f, 3f)] public float cautionRiseMultiplier = 1.4f;

        [Tooltip("Awareness rise speed multiplier applied to all units during Alert.")]
        [Range(1f, 3f)] public float alertRiseMultiplier = 2.0f;

        [Tooltip("If true, movement speed scales globally with alert level.")]
        public bool scaleSpeedWithAlert = true;

        [Tooltip("Speed multiplier at Caution level (if scaleSpeedWithAlert is on).")]
        [Range(1f, 2f)] public float cautionSpeedMultiplier = 1.15f;

        [Tooltip("Speed multiplier at Alert level (if scaleSpeedWithAlert is on).")]
        [Range(1f, 2f)] public float alertSpeedMultiplier = 1.35f;

        [Header("Alert Events")]
        public UnityEvent onAlertLevelNormal;
        public UnityEvent onAlertLevelCaution;
        public UnityEvent onAlertLevelAlert;

        // ---------- Runtime ---------------------------------------------------

        public float TensionLevel { get; private set; }

        /// <summary>
        /// Seconds the player has been undetected since last direct sighting.
        /// Resets when any unit gets direct visual contact.
        /// </summary>
        public static float EvasionTime { get; private set; }

        /// <summary>Current scene-wide alert level.</summary>
        public static SceneAlertLevel AlertLevel { get; private set; } = SceneAlertLevel.Normal;

        // ---------- Static registry -------------------------------------------

        private static StealthTarget _target;
        private static List<StealthHuntAI> _units = new List<StealthHuntAI>();
        public static IReadOnlyList<StealthHuntAI> AllUnits => _units;

        /// <summary>
        /// Called when a unit becomes Hostile.
        /// Propagates alert to all units within alertRadius -- forces them Hostile too.
        /// Simulates radio communication and squad coordination.
        /// </summary>
        // Guard against recursive AlertSquad calls
        // AlertSystem drives tension and alert level
        private readonly AlertSystem _alertSystem = new AlertSystem();
        public static AlertSystem Alert => _instance?._alertSystem;

        private static bool _alertingSquad = false;

        public static void AlertSquad(StealthHuntAI source, float alertRadius = 40f)
        {
            if (_alertingSquad) return;
            _alertingSquad = true;

            try
            {
                Vector3 sourcePos = source.transform.position;
                var sourceBoard = SquadBlackboard.Get(source.squadID);

                for (int i = 0; i < _units.Count; i++)
                {
                    var unit = _units[i];
                    if (unit == null || unit == source) continue;
                    if (unit.squadID != source.squadID) continue;

                    float dist = Vector3.Distance(sourcePos, unit.transform.position);

                    // Always alert non-hostile squad members
                    if (unit.CurrentAlertState != AlertState.Hostile)
                        unit.ForceHostileSilent();

                    // Share intel with ALL squad members including already-hostile ones
                    if (sourceBoard != null)
                    {
                        var board = SquadBlackboard.Get(unit.squadID);
                        if (dist <= alertRadius)
                            board?.ShareIntel(sourceBoard.SharedLastKnown,
                                sourceBoard.SharedConfidence * 0.8f);
                        else
                            board?.ShareIntel(sourcePos, 0.2f);
                    }
                }
            }
            finally
            {
                _alertingSquad = false;
            }
        }
        private static List<SquadBlackboard> _squads = new List<SquadBlackboard>();

        public static SquadBlackboard GetBlackboard(int squadID)
        {
            for (int i = 0; i < _squads.Count; i++)
                if (_squads[i] != null && _squads[i].SquadID == squadID)
                    return _squads[i];
            return null;
        }

        // ---------- Internal --------------------------------------------------

        private float _nudgeTimer;
        private float _squadEvalTimer;
        private float _alertCooldownTimer;
        private float _cautionCooldownTimer;
        private float _evasionTimer;

        // Per-unit nudge cooldown -- key: unit instanceID, value: last nudge time
        private static readonly Dictionary<int, float> _nudgeCooldowns
            = new Dictionary<int, float>();

        // ---------- Flight pattern memory (Markov model) --------------------

        // Directional space divided into 8 sectors (N, NE, E, SE, S, SW, W, NW)
        // Transition matrix: _markov[from, to] = observation count
        private static readonly float[,] _markov = new float[8, 8];

        // Prior strength -- flat prior so early predictions aren't wild
        private const float MarkovPrior = 0.5f;

        // Legacy weighted average for blending with Markov prediction
        private static readonly Queue<Vector3> _flightHistory = new Queue<Vector3>();
        private const int MaxFlightHistory = 5;

        /// <summary>
        /// Predicted flight direction based on Markov model of player escape patterns.
        /// Blends Markov prediction with weighted history average.
        /// Accuracy improves with more encounters.
        /// </summary>
        public static Vector3 PredictedFlightDir => FlightMemory.PredictedFlightDir;

        /// <summary>Total flight observations recorded this session.</summary>
        public static int FlightObservations => FlightMemory.Observations;

        // ---------- Hide spot memory -----------------------------------------

        public struct HideSpotRecord
        {
            public Vector3 Position;
            public int FoundCount;
            public float LastFoundTime;
        }

        private static readonly List<HideSpotRecord> _knownHideSpots
            = new List<HideSpotRecord>();
        private const int MaxHideSpots = 10;
        private const float HideSpotMergeRadius = 3f;

        public static System.Collections.Generic.IReadOnlyList<HideSpotRecord> KnownHideSpots
            => _knownHideSpots;

        // Reused NavMeshPath for sound propagation -- initialized lazily
        private static NavMeshPath _soundPath;

        // Intensity threshold below which we skip NavMesh path and use raycast only
        private const float NavMeshSoundThreshold = 0.4f;

        // ---------- Squad Heat Map -------------------------------------------

        private static readonly Dictionary<Vector2Int, float> _heatMap
            = new Dictionary<Vector2Int, float>();
        private const float HeatGridSize = 2f;
        private const float HeatDecayRate = 0.5f;

        public static void RegisterHeat(Vector3 pos, float heat = 1f)
        {
            var cell = WorldToCell(pos);
            _heatMap[cell] = Mathf.Clamp01(
                (_heatMap.TryGetValue(cell, out float cur) ? cur : 0f) + heat);
        }

        public static void ClearHeat(Vector3 pos)
            => _heatMap.Remove(WorldToCell(pos));

        public static float GetHeat(Vector3 pos)
            => _heatMap.TryGetValue(WorldToCell(pos), out float h) ? h : 0f;

        private static Vector2Int WorldToCell(Vector3 pos)
            => new Vector2Int(
                Mathf.RoundToInt(pos.x / HeatGridSize),
                Mathf.RoundToInt(pos.z / HeatGridSize));

        // ---------- CoverPoint Registry --------------------------------------

        // Uses object to avoid Core depending on Combat assembly
        private static readonly List<System.Object> _coverPoints = new List<System.Object>();
        public static IReadOnlyList<System.Object> AllCoverPoints => _coverPoints;

        public static void RegisterCoverPoint(System.Object cp)
        {
            if (!_coverPoints.Contains(cp)) _coverPoints.Add(cp);
        }

        public static void UnregisterCoverPoint(System.Object cp)
            => _coverPoints.Remove(cp);

        // ---------- Sector Watch ---------------------------------------------

        private static readonly Dictionary<int, StealthHuntAI> _sectorWatchers
            = new Dictionary<int, StealthHuntAI>();

        public static void RegisterSectorWatch(Vector3 direction, StealthHuntAI unit)
        {
            _sectorWatchers[DirectionToSector8(direction)] = unit;
        }

        public static void ClearSectorWatch(StealthHuntAI unit)
        {
            var keys = new List<int>();
            foreach (var kv in _sectorWatchers)
                if (kv.Value == unit) keys.Add(kv.Key);
            foreach (var k in keys) _sectorWatchers.Remove(k);
        }

        public static bool IsSectorWatched(Vector3 direction)
        {
            if (!_sectorWatchers.TryGetValue(
                DirectionToSector8(direction), out var w)) return false;
            return w != null;
        }

        private static int DirectionToSector8(Vector3 dir)
        {
            dir.y = 0f;
            float deg = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (deg < 0) deg += 360f;
            return Mathf.RoundToInt(deg / 45f) % 8;
        }

        // Auto-generated regions (used when no PatrolRegion components exist)
        private readonly List<PatrolRegion> _autoRegions = new List<PatrolRegion>();
        private bool _autoRegionsGenerated;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            // Wire inspector fields to AlertSystem
            _alertSystem.TensionDecaySpeed = tensionDecaySpeed;
            _alertSystem.TensionRiseSpeed = tensionRiseSpeed;
            _alertSystem.TensionZoneRadius = tensionZoneRadius;
            _alertSystem.ProximityWeight = proximityWeight;
            _alertSystem.EvasionWeight = evasionWeight;
            _alertSystem.EvasionTimeToMax = evasionTimeToMaxTension;
            _alertSystem.AlertCooldownTime = alertCooldownTime;
            _alertSystem.CautionCooldownTime = cautionCooldownTime;
            _alertSystem.CautionRiseMultiplier = cautionRiseMultiplier;
            _alertSystem.AlertRiseMultiplier = alertRiseMultiplier;
            _alertSystem.ScaleSpeedWithAlert = scaleSpeedWithAlert;
            _alertSystem.CautionSpeedMultiplier = cautionSpeedMultiplier;
            _alertSystem.AlertSpeedMultiplier = alertSpeedMultiplier;
            _alertSystem.MinIntelConfidence = minIntelConfidence;
            _alertSystem.OnNormal = onAlertLevelNormal;
            _alertSystem.OnCaution = onAlertLevelCaution;
            _alertSystem.OnAlert = onAlertLevelAlert;

            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[HuntDirector] Duplicate instance found. Destroying.");
                Destroy(gameObject);
                return;
            }
            _instance = this;

            // Auto-register all scene lights into LightRegistry
            // so AwarenessSensor can query them without FindObjectsByType
            LightRegistry.AutoRegisterSceneLights();
        }

        private void OnDestroy()
        {
            if (_instance != this) return;

            _instance = null;
            _target = null;

            // Clear lists but keep references null-safe
            // Units and squads unregister themselves in their own OnDestroy
            _units.Clear();
            _squads.Clear();
        }

        private void Update()
        {
            // Decay heat map
            if (_heatMap.Count > 0)
            {
                var cells = new List<Vector2Int>(_heatMap.Keys);
                for (int i = 0; i < cells.Count; i++)
                {
                    float v = _heatMap[cells[i]] - HeatDecayRate * Time.deltaTime;
                    if (v <= 0f) _heatMap.Remove(cells[i]);
                    else _heatMap[cells[i]] = v;
                }
            }

            UpdateTension();
            UpdateAlertLevel();

            _nudgeTimer += Time.deltaTime;
            _squadEvalTimer += Time.deltaTime;

            if (enableNudging && _nudgeTimer >= nudgeInterval)
            {
                _nudgeTimer = 0f;
                if (TensionLevel >= nudgeThreshold)
                {
                    EnsureRegions();
                    TryNudgeUnits();
                }
            }

            if (_squadEvalTimer >= 0.5f)
            {
                _squadEvalTimer = 0f;
                EvaluateSquadRoles();
                DecaySquadIntel();
            }
        }

        // ---------- Tension ---------------------------------------------------

        private void UpdateTension()
        {
            if (_target == null || !_target.IsActive)
            {
                TensionLevel = Mathf.MoveTowards(
                    TensionLevel, 0f, tensionDecaySpeed * 2f * Time.deltaTime);
                return;
            }

            // Tension decays fast when any unit is Hostile --
            // Director backs off when units already have the situation handled
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i] == null) continue;
                if (_units[i].CurrentAlertState == AlertState.Hostile)
                {
                    // Reset evasion timer -- player is actively being chased
                    EvasionTime = 0f;
                    TensionLevel = Mathf.MoveTowards(
                        TensionLevel, 0f, tensionDecaySpeed * 3f * Time.deltaTime);
                    return;
                }
            }

            // Check if any unit has direct visual contact -- reset evasion timer
            bool anyDirectContact = false;
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i] == null) continue;
                var sensor = _units[i].GetComponent<AwarenessSensor>();
                if (sensor != null && sensor.CanSeeTarget)
                {
                    anyDirectContact = true;
                    break;
                }
            }

            if (anyDirectContact)
                EvasionTime = 0f;
            else
                EvasionTime += Time.deltaTime;

            // ---------- Proximity factor --------------------------------------
            float proximityFactor = 0f;
            if (proximityWeight > 0f)
            {
                float closestDist = float.MaxValue;
                for (int i = 0; i < _units.Count; i++)
                {
                    if (_units[i] == null) continue;
                    Vector3 uPos = _units[i].transform.position;
                    Vector3 tPos = _target.Position;
                    float hDist = Mathf.Sqrt(
                        (uPos.x - tPos.x) * (uPos.x - tPos.x) +
                        (uPos.z - tPos.z) * (uPos.z - tPos.z));
                    if (hDist < closestDist) closestDist = hDist;
                }
                proximityFactor = 1f - Mathf.Clamp01(closestDist / tensionZoneRadius);
            }

            // ---------- Evasion time factor -----------------------------------
            float evasionFactor = 0f;
            if (evasionWeight > 0f)
            {
                evasionFactor = Mathf.Clamp01(EvasionTime / evasionTimeToMaxTension);
            }

            // ---------- Blended rise ------------------------------------------
            float blended = proximityFactor * proximityWeight
                          + evasionFactor * evasionWeight;

            float rise = tensionRiseSpeed * Mathf.Max(0.1f, blended);

            TensionLevel = Mathf.MoveTowards(
                TensionLevel, 1f, rise * Time.deltaTime);
        }

        // ---------- Alert level -----------------------------------------------

        private void UpdateAlertLevel()
        {
            // Count unit states
            bool anyHostile = false;
            bool anySuspicious = false;

            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i] == null) continue;
                if (_units[i].CurrentAlertState == AlertState.Hostile)
                    anyHostile = true;
                else if (_units[i].CurrentAlertState == AlertState.Suspicious)
                    anySuspicious = true;
            }

            SceneAlertLevel desired;

            if (anyHostile)
            {
                desired = SceneAlertLevel.Alert;
                _alertCooldownTimer = 0f;
                _cautionCooldownTimer = 0f;
            }
            else if (anySuspicious)
            {
                // Cooling down from Alert
                if (AlertLevel == SceneAlertLevel.Alert)
                {
                    _alertCooldownTimer += Time.deltaTime;
                    desired = _alertCooldownTimer >= alertCooldownTime
                        ? SceneAlertLevel.Caution
                        : SceneAlertLevel.Alert;
                }
                else
                {
                    desired = SceneAlertLevel.Caution;
                    _cautionCooldownTimer = 0f;
                }
            }
            else
            {
                // All units passive
                if (AlertLevel == SceneAlertLevel.Alert)
                {
                    _alertCooldownTimer += Time.deltaTime;
                    if (_alertCooldownTimer >= alertCooldownTime)
                        AlertLevel = SceneAlertLevel.Caution;
                    desired = AlertLevel;
                }
                else if (AlertLevel == SceneAlertLevel.Caution)
                {
                    _cautionCooldownTimer += Time.deltaTime;
                    desired = _cautionCooldownTimer >= cautionCooldownTime
                        ? SceneAlertLevel.Normal
                        : SceneAlertLevel.Caution;
                }
                else
                {
                    desired = SceneAlertLevel.Normal;
                }
            }

            if (desired != AlertLevel)
            {
                AlertLevel = desired;
                OnAlertLevelChanged(AlertLevel);
            }
        }

        private void OnAlertLevelChanged(SceneAlertLevel level)
        {
            // Fire UnityEvents
            switch (level)
            {
                case SceneAlertLevel.Normal: onAlertLevelNormal.Invoke(); break;
                case SceneAlertLevel.Caution: onAlertLevelCaution.Invoke(); break;
                case SceneAlertLevel.Alert: onAlertLevelAlert.Invoke(); break;
            }

            // Apply speed and sensor multipliers
            for (int i = 0; i < _units.Count; i++)
                ApplyAlertEffectsToUnit(_units[i], level);

            // Share intel ONCE when rising to Alert -- never per-frame
            if (level == SceneAlertLevel.Alert)
            {
                for (int u = 0; u < _units.Count; u++)
                {
                    if (_units[u] == null) continue;
                    for (int s = 0; s < _squads.Count; s++)
                    {
                        if (_squads[s] == null) continue;
                        if (_squads[s].SharedConfidence > minIntelConfidence)
                            _units[u].ReceiveSquadIntel(
                                _squads[s].SharedLastKnown,
                                _squads[s].SharedConfidence * 0.5f);
                    }
                }
            }
        }

        private void ApplyAlertEffectsToUnit(StealthHuntAI unit, SceneAlertLevel level)
        {
            if (unit == null) return;

            var sensor = unit.GetComponent<AwarenessSensor>();
            if (sensor == null) return;

            // Scale rise speed based on alert level
            float riseMultiplier = 1f;
            float speedMultiplier = 1f;

            switch (level)
            {
                case SceneAlertLevel.Caution:
                    riseMultiplier = cautionRiseMultiplier;
                    speedMultiplier = scaleSpeedWithAlert ? cautionSpeedMultiplier : 1f;
                    break;

                case SceneAlertLevel.Alert:
                    riseMultiplier = alertRiseMultiplier;
                    speedMultiplier = scaleSpeedWithAlert ? alertSpeedMultiplier : 1f;
                    break;

                case SceneAlertLevel.Normal:
                    riseMultiplier = 1f;
                    speedMultiplier = 1f;
                    break;
            }

            unit.ApplyAlertLevelEffects(riseMultiplier, speedMultiplier);

            // Intel sharing is handled separately via ShareIntelOnAlert()
            // called once when alert level changes -- not per-unit per-frame
        }

        // ---------- Director nudge --------------------------------------------

        private void EnsureRegions()
        {
            // If scene has manual PatrolRegion components use those
            if (PatrolRegion.All.Count > 0) return;

            // Auto-generate regions from NavMesh sampling once
            if (_autoRegionsGenerated) return;
            _autoRegionsGenerated = true;

            if (_target == null) return;

            float scanRadius = regionSearchRange;
            float angleStep = 360f / autoRegionCount;
            int created = 0;

            for (int i = 0; i < autoRegionCount * 2 && created < autoRegionCount; i++)
            {
                float angle = angleStep * i + Random.Range(-20f, 20f);
                float dist = Random.Range(scanRadius * 0.3f, scanRadius * 0.9f);
                Vector3 candidate = _target.Position + new Vector3(
                    Mathf.Cos(angle * Mathf.Deg2Rad) * dist,
                    0f,
                    Mathf.Sin(angle * Mathf.Deg2Rad) * dist);

                if (!NavMeshHelper.Sample(candidate, autoRegionRadius,
                                           out Vector3 snapped)) continue;

                // Create a hidden GameObject with PatrolRegion
                var go = new GameObject("AutoRegion_" + created);
                go.hideFlags = HideFlags.HideInHierarchy;
                go.transform.position = snapped;

                var region = go.AddComponent<PatrolRegion>();
                region.radius = autoRegionRadius;
                region.interestBias = 1f;

                _autoRegions.Add(region);
                created++;
            }

            Debug.Log("[HuntDirector] Auto-generated " + created +
                      " patrol regions. Add PatrolRegion components to override.");
        }

        private void TryNudgeUnits()
        {
            if (_target == null) return;

            // Score all patrol regions -- prefer manual ones, fall back to auto
            var regions = PatrolRegion.All.Count > 0
                ? PatrolRegion.All
                : (System.Collections.Generic.IReadOnlyList<PatrolRegion>)_autoRegions;

            if (regions.Count == 0) return;

            Vector3 lastKnown = _target.Position;
            Vector3 flightVec = _target.FlightVector;

            PatrolRegion bestRegion = null;
            float bestScore = -1f;

            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                float s = r.Evaluate(lastKnown, flightVec, regionSearchRange);
                r.CurrentScore = s;
                if (s > bestScore) { bestScore = s; bestRegion = r; }
            }

            if (bestRegion == null || bestScore < 0.05f) return;

            // Find best passive unit -- same floor preferred, respect cooldown
            StealthHuntAI candidate = null;
            float bestUScore = -1f;

            for (int i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                if (unit == null) continue;
                if (unit.AwarenessLevel > 0.25f) continue;
                if (unit.CurrentAlertState != AlertState.Passive) continue;

                // Respect per-unit nudge cooldown
                float lastNudge = 0f;
                _nudgeCooldowns.TryGetValue(unit.GetInstanceID(), out lastNudge);
                if (Time.time - lastNudge < nudgeCooldown) continue;

                // Skip if unit is already close to the region
                if (Vector3.Distance(unit.transform.position,
                    bestRegion.transform.position) < bestRegion.radius) continue;

                float distToRegion = Vector3.Distance(
                    unit.transform.position, bestRegion.transform.position);
                float uScore = 1f - Mathf.Clamp01(distToRegion / regionSearchRange);

                bool sameFloor;
                if (unit.CurrentFloorID >= 0 && _target.CurrentFloorID >= 0)
                    sameFloor = unit.CurrentFloorID == _target.CurrentFloorID;
                else
                    sameFloor = Mathf.Abs(unit.transform.position.y
                                        - _target.Position.y) <= 2.5f;

                if (sameFloor) uScore += 0.3f;
                if (uScore > bestUScore) { bestUScore = uScore; candidate = unit; }
            }

            if (candidate == null) return;

            // Send unit to a random point within the chosen region
            Vector3 regionCenter = bestRegion.transform.position;
            float angle = Random.Range(0f, 360f);
            float dist = Random.Range(0f, bestRegion.radius * 0.7f);
            Vector3 nudgeTarget = regionCenter + new Vector3(
                Mathf.Cos(angle * Mathf.Deg2Rad) * dist,
                0f,
                Mathf.Sin(angle * Mathf.Deg2Rad) * dist);

            if (NavMeshHelper.Sample(nudgeTarget, bestRegion.radius, out Vector3 snapped))
            {
                candidate.SetNudgeDestination(snapped);
                _nudgeCooldowns[candidate.GetInstanceID()] = Time.time;
                bestRegion.LastVisitTime = Time.time;
            }

            // No awareness bump -- region routing is sufficient.
            // Bumping awareness via ReceiveSquadIntel causes LastStimulusPosition
            // to update constantly which looks unnatural in debug view.
        }
        // ---------- Squad management ------------------------------------------

        private void EvaluateSquadRoles()
        {
            for (int i = 0; i < _squads.Count; i++)
                _squads[i]?.EvaluateRoles(_target);
        }

        private void DecaySquadIntel()
        {
            for (int i = 0; i < _squads.Count; i++)
                _squads[i]?.DecayIntel(intelDecayTime);
        }

        private float GetHighestAwareness()
        {
            float max = 0f;
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i] != null)
                    max = Mathf.Max(max, _units[i].AwarenessLevel);
            }
            return max;
        }

        // ---------- Static registration API -----------------------------------

        public static void RegisterTarget(StealthTarget target)
        {
            _target = target;
            for (int i = 0; i < _units.Count; i++)
                _units[i]?.ReceiveTarget(target);
        }

        public static void UnregisterTarget(StealthTarget target)
        {
            if (_target == target) _target = null;
        }

        public static void RegisterUnit(StealthHuntAI unit)
        {
            if (!_units.Contains(unit))
                _units.Add(unit);

            EnsureSquad(unit);

            if (_target != null)
                unit.ReceiveTarget(_target);

            // Apply current alert level effects immediately
            if (_instance != null && AlertLevel != SceneAlertLevel.Normal)
                _instance.ApplyAlertEffectsToUnit(unit, AlertLevel);
        }

        public static void UnregisterUnit(StealthHuntAI unit)
        {
            _units.Remove(unit);
            for (int i = 0; i < _squads.Count; i++)
                _squads[i]?.RemoveUnit(unit);
        }

        public static void ReportStateChange(StealthHuntAI unit,
                                              AlertState newAlert, SubState newSub)
        {
            SquadBlackboard squad = GetSquadFor(unit);
            squad?.OnUnitStateChanged(unit, newAlert, newSub);
        }

        /// <summary>
        /// Record a flight vector when a unit loses the player.
        /// Updates the Markov transition model and weighted history.
        /// </summary>
        public static void RecordFlightVector(Vector3 flightVec)
        {
            FlightMemory.RecordFlight(flightVec);
        }

        private static void UpdatePredictedFlight()
        {
            // Delegated to FlightMemory -- called automatically by RecordFlight
        }

        // ---------- Markov helpers --------------------------------------------

        /// <summary>Convert a direction vector to one of 8 compass sectors (0=N, clockwise).</summary>
        private static int DirectionToSector(Vector3 dir)
        {
            dir.y = 0f;
            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            return Mathf.RoundToInt(angle / 45f) % 8;
        }

        /// <summary>Convert a sector index back to a world direction vector.</summary>
        private static Vector3 SectorToDirection(int sector)
        {
            float angle = sector * 45f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }

        /// <summary>
        /// Record a position where the player was spotted.
        /// Used to build hide spot memory for better searching.
        /// </summary>
        public static void RecordPlayerPosition(Vector3 position)
        {
            // Check if close to existing record
            for (int i = 0; i < _knownHideSpots.Count; i++)
            {
                if (Vector3.Distance(_knownHideSpots[i].Position, position)
                    < HideSpotMergeRadius)
                {
                    var updated = _knownHideSpots[i];
                    updated.FoundCount++;
                    updated.LastFoundTime = Time.time;
                    _knownHideSpots[i] = updated;
                    return;
                }
            }

            // New spot
            _knownHideSpots.Add(new HideSpotRecord
            {
                Position = position,
                FoundCount = 1,
                LastFoundTime = Time.time
            });

            // Keep only most recent/frequent -- drop oldest if over limit
            if (_knownHideSpots.Count > MaxHideSpots)
            {
                // Remove least recently found
                int oldest = 0;
                for (int i = 1; i < _knownHideSpots.Count; i++)
                    if (_knownHideSpots[i].LastFoundTime < _knownHideSpots[oldest].LastFoundTime)
                        oldest = i;
                _knownHideSpots.RemoveAt(oldest);
            }
        }

        /// <summary>
        /// Returns a snapshot copy of known hide spots for use in search strategy.
        /// Caller owns the list -- safe to use in coroutines.
        /// </summary>
        public static List<HideSpotRecord> GetHideSpotSnapshot()
        {
            return new List<HideSpotRecord>(_knownHideSpots);
        }

        public static StealthTarget GetTarget() => _target;

        /// <summary>Get the shared blackboard for a given squad ID.</summary>
        public static SquadBlackboard GetSquadBlackboard(int squadID)
        {
            for (int i = 0; i < _squads.Count; i++)
            {
                if (_squads[i].SquadID == squadID)
                    return _squads[i];
            }
            return null;
        }

        /// <summary>
        /// Broadcast a sound stimulus to all units within radius.
        /// Uses linear distance falloff.
        /// Called by SoundStimulus.Emit().
        /// </summary>
        public static void BroadcastSound(Vector3 position, float intensity, float radius)
        {
            SoundSystem.Broadcast(position, intensity, radius);
        }

        private static void PropagateRaycast(
            Vector3 soundPos, Vector3 unitPos,
            float dist, float radius, float intensity,
            out float scaledIntensity, out Vector3 arrivalDir)
        {
            // Distance falloff
            float falloff = 1f - Mathf.Clamp01(dist / radius);
            scaledIntensity = intensity * falloff;
            arrivalDir = Vector3.zero; // direct direction

            // Count walls between unit and sound
            Vector3 toSound = soundPos - unitPos;
            int hitCount = Physics.RaycastNonAlloc(
                unitPos, toSound.normalized,
                _raycastBuffer, dist, _sightBlockerMask);

            int walls = 0;
            for (int h = 0; h < hitCount; h++)
                walls++;

            if (walls >= 2) scaledIntensity *= 0.05f;
            else if (walls == 1) scaledIntensity *= 0.25f;
        }

        private static void PropagateNavMesh(
            Vector3 soundPos, Vector3 unitPos,
            float dist, float radius, float intensity,
            out float scaledIntensity, out Vector3 arrivalDir)
        {
            arrivalDir = Vector3.zero;

            // First check direct line -- if clear use it directly
            Vector3 toSound = soundPos - unitPos;
            // Use registered mask or fall back to Default layer only
            LayerMask mask = _sightBlockerSet
                ? _sightBlockerMask
                : LayerMask.GetMask("Default");
            bool hasLine = !Physics.Raycast(unitPos, toSound.normalized,
                                                 dist, mask);

            if (hasLine)
            {
                float falloff = 1f - Mathf.Clamp01(dist / radius);
                scaledIntensity = intensity * falloff;
                return;
            }

            // Occluded -- calculate NavMesh path to get corner count
            bool pathFound = NavMesh.CalculatePath(
                unitPos, soundPos, NavMesh.AllAreas, _soundPath);

            if (!pathFound || _soundPath.status == NavMeshPathStatus.PathInvalid)
            {
                // No path -- very muffled
                float falloff = 1f - Mathf.Clamp01(dist / radius);
                scaledIntensity = intensity * falloff * 0.05f;
                return;
            }

            // Count corners -- each corner = sound going around a wall
            int corners = Mathf.Max(0, _soundPath.corners.Length - 2);

            // Arrival direction = toward first corner from unit
            if (_soundPath.corners.Length >= 2)
                arrivalDir = (_soundPath.corners[1] - unitPos).normalized;

            // NavMesh path length for distance falloff
            float pathLength = 0f;
            for (int c = 1; c < _soundPath.corners.Length; c++)
                pathLength += Vector3.Distance(_soundPath.corners[c - 1],
                                               _soundPath.corners[c]);

            float pathFalloff = 1f - Mathf.Clamp01(pathLength / radius);

            // Corner penalty -- each corner muffles significantly
            // Gunshots travel well around corners -- use higher base
            float cornerBase = intensity >= 0.8f ? 0.8f : 0.55f;
            float cornerPenalty = Mathf.Pow(cornerBase, corners);

            scaledIntensity = intensity * pathFalloff * cornerPenalty;
        }

        // Shared raycast buffer and layer mask for sound propagation
        private static readonly RaycastHit[] _raycastBuffer = new RaycastHit[16];
        private static LayerMask _sightBlockerMask = 0;
        private static bool _sightBlockerSet = false;

        /// <summary>Called by AwarenessSensor to share its sight blocker mask.</summary>
        public static void RegisterSightBlockers(LayerMask mask)
        {
            _sightBlockerMask = mask;
            _sightBlockerSet = true;
        }

        /// <summary>
        /// Broadcast a sound stimulus using an AnimationCurve for falloff.
        /// Uses same hybrid propagation as BroadcastSound.
        /// </summary>
        public static void BroadcastSoundWithCurve(Vector3 position, float intensity,
                                                     float radius, AnimationCurve curve)
        {
            if (_soundPath == null)
                _soundPath = new NavMeshPath();

            for (int i = 0; i < _units.Count; i++)
            {
                var unit = _units[i];
                if (unit == null) continue;

                Vector3 unitPos = unit.transform.position + Vector3.up * 0.5f;
                float dist = Vector3.Distance(unitPos, position);
                if (dist > radius) continue;

                float scaledIntensity;
                Vector3 arrivalDir;

                if (intensity < NavMeshSoundThreshold)
                    PropagateRaycast(position, unitPos, dist, radius,
                                     intensity, out scaledIntensity, out arrivalDir);
                else
                    PropagateNavMesh(position, unitPos, dist, radius,
                                     intensity, out scaledIntensity, out arrivalDir);

                // Apply curve on top of propagation result
                float t = Mathf.Clamp01(dist / radius);
                float curveMult = curve != null ? curve.Evaluate(t) : 1f;
                scaledIntensity *= curveMult;

                if (scaledIntensity <= 0.01f) continue;

                unit.GetComponent<AwarenessSensor>()
                    ?.HearSound(position, scaledIntensity, arrivalDir);

                // Near high-intensity shot -- passive/suspicious guards react immediately
                if (intensity >= 0.8f && dist <= 12f)
                    unit.OnNearShotHeard(position, scaledIntensity);
            }
        }

        // ---------- Squad helpers ---------------------------------------------

        private static void EnsureSquad(StealthHuntAI unit)
        {
            int id = unit.squadID;

            if (id == 0)
            {
                for (int i = 0; i < _squads.Count; i++)
                {
                    if (_squads[i].UnitCount < 6)
                    {
                        _squads[i].AddUnit(unit);
                        unit.AssignSquadID(_squads[i].SquadID);
                        return;
                    }
                }

                var newSquad = new SquadBlackboard(_squads.Count + 1);
                newSquad.AddUnit(unit);
                unit.AssignSquadID(newSquad.SquadID);
                _squads.Add(newSquad);
            }
            else
            {
                SquadBlackboard found = null;
                for (int i = 0; i < _squads.Count; i++)
                {
                    if (_squads[i].SquadID == id)
                    {
                        found = _squads[i];
                        break;
                    }
                }

                if (found == null)
                {
                    found = new SquadBlackboard(id);
                    _squads.Add(found);
                }

                found.AddUnit(unit);
            }
        }

        private static SquadBlackboard GetSquadFor(StealthHuntAI unit)
        {
            for (int i = 0; i < _squads.Count; i++)
            {
                if (_squads[i].Contains(unit)) return _squads[i];
            }
            return null;
        }

        // ---------- Gizmos ----------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying || _target == null) return;

            float t = TensionLevel;
            Gizmos.color = new Color(1f, 1f - t, 0f, 0.1f + t * 0.3f);
            Gizmos.DrawWireSphere(_target.Position, tensionZoneRadius * Mathf.Max(t, 0.1f));

            UnityEditor.Handles.Label(
                transform.position + Vector3.up,
                "Tension: " + TensionLevel.ToString("F2") +
                " | Evasion: " + EvasionTime.ToString("F0") + "s" +
                " | FlightObs: " + FlightObservations +
                "  Alert: " + AlertLevel.ToString());
        }
#endif
    }
}