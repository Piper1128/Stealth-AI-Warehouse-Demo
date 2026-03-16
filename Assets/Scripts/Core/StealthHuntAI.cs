using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace StealthHuntAI
{
    // ---------- Enums ---------------------------------------------------------

    public enum AlertState
    {
        Passive,
        Suspicious,
        Hostile
    }

    public enum SubState
    {
        // Passive
        Idle,
        Patrolling,
        GuardZonePatrol,
        Returning,

        // Suspicious
        Alerted,
        Investigating,
        Searching,

        // Hostile
        Pursuing,
        Flanking,
        LostTarget
    }

    public enum SquadRole
    {
        Dynamic,
        Tracker,
        Flanker,
        Overwatch,
        Blocker,
        Caller
    }

    public enum Personality
    {
        Cautious,
        Balanced,
        Aggressive
    }

    public enum BehaviourMode
    {
        Idle,       // stands still, scans with eyes only
        Patrol,     // follows patrolPoints in order
        GuardZone   // auto-generates patrol inside a defined zone
    }

    public enum PatrolPattern
    {
        Loop,       // A -> B -> C -> A
        PingPong    // A -> B -> C -> B -> A
    }

    public enum MoraleState
    {
        High,       // 0.7 - 1.0  confident, standard behaviour
        Medium,     // 0.3 - 0.7  cautious or frustrated
        Low         // 0.0 - 0.3  broken or desperate
    }

    // ---------- Main component ------------------------------------------------

    /// <summary>
    /// Drop this on any enemy unit. Everything else is automatic.
    /// Requires NavMeshAgent on the same GameObject (added automatically if missing).
    /// Reads Animator if present -- no setup needed.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/Stealth Hunt AI")]
    [DisallowMultipleComponent]
    public partial class StealthHuntAI : MonoBehaviour
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Perception")]
        [Range(1f, 60f)] public float sightRange = 15f;
        [Range(10f, 360f)] public float sightAngle = 90f;
        [Range(1f, 40f)] public float hearingRange = 10f;

        [Tooltip("How fast sight exposure builds up. " +
                 "Higher = faster detection. 1.5 = ~0.67s at full visibility. " +
                 "Hostile state uses 8x this, Suspicious 3x.")]
        [Range(0.1f, 10f)] public float sightDetectionSpeed = 1.5f;

        [Tooltip("How fast sight exposure drains when target leaves FOV. "
                 + "Lower = exposure persists longer after brief cover.")]
        [Range(0.05f, 3f)] public float sightDecaySpeed = 0.3f;

        [Header("Awareness Thresholds")]
        [Range(0f, 1f)] public float suspicionThreshold = 0.30f;
        [Range(0f, 1f)] public float hostileThreshold = 0.70f;

        [Header("Behaviour")]
        public Personality personality = Personality.Balanced;
        public BehaviourMode behaviourMode = BehaviourMode.Idle;

        [Tooltip("Speed multiplier when patrolling or guarding. " +
                 "Applied on top of base speed. 0.5 = half speed patrol.")]
        [Range(0.1f, 1f)] public float patrolSpeedMultiplier = 0.55f;

        [Tooltip("Speed multiplier when actively pursuing or searching hostile.")]
        [Range(0.5f, 2f)] public float chaseSpeedMultiplier = 1.0f;

        [Tooltip("Patrol waypoints. Used when BehaviourMode is Patrol.")]
        public Transform[] patrolPoints;

        [Tooltip("Loop: A->B->C->A  PingPong: A->B->C->B->A")]
        public PatrolPattern patrolPattern = PatrolPattern.Loop;

        [Tooltip("How long the unit waits at each waypoint before moving on (seconds).")]
        [Range(0f, 10f)] public float waypointWaitTime = 0f;

        [Header("Guard Zone")]
        [Tooltip("Center of the guarded area. Defaults to this object's spawn position if not set.")]
        public Transform guardZoneCenter;

        [Tooltip("Radius of the guarded area.")]
        [Range(2f, 50f)] public float guardZoneRadius = 10f;

        [Tooltip("Number of waypoints auto-generated inside the Guard Zone. " +
                 "Set to 0 for static guard -- unit stands still and scans.")]
        [Range(0, 12)] public int guardZoneWaypointCount = 4;

        [Tooltip("How long the unit waits at each guard zone waypoint.")]
        [Range(0f, 15f)] public float guardZoneWaitTime = 1.5f;

        [Tooltip("Eye height above transform pivot for sight raycasts. " +
                 "Ignored when a head bone is found automatically.")]
        [Range(0.5f, 2.5f)] public float eyeHeight = 1.6f;

        [Header("Search")]
        [Range(1f, 300f)] public float searchDuration = 10f;
        [Range(2f, 100f)] public float searchRadius = 8f;
        [Range(2, 8)] public int searchPointCount = 4;

        [Tooltip("Vertical range for NavMesh sampling. " +
                 "Set to 0 for auto (uses NavMeshHelper default). " +
                 "Increase for tall multi-story levels with many floors.")]
        [Range(0f, 20f)] public float searchHeightRange = 0f;

        [Header("Movement")]
        [Tooltip("Custom movement provider. Leave empty to use NavMeshMovement (auto-added). " +
                 "Assign any component implementing IStealthMovement for custom pathfinding.")]
        public MonoBehaviour movementProvider;

        [Header("Search Strategy")]
        [Tooltip("Override the default ReachabilitySearch with a custom strategy. " +
                 "Leave empty to auto-configure ReachabilitySearch.")]
        public MonoBehaviour searchStrategyOverride;

        [Tooltip("Cell size in meters for visited location memory. " +
                 "Smaller = more precise but more memory.")]
        [Range(1f, 6f)] public float visitedCellSize = 3f;

        [Header("Squad")]
        [Tooltip("Leave 0 for auto-assignment by HuntDirector.")]
        public int squadID = 0;

        [Tooltip("Dynamic = assigned at runtime. Set a fixed role for specialist units.")]
        public SquadRole manualRole = SquadRole.Dynamic;

        [Header("Animation (auto-detected)")]
        public Animator animator;
        public string animParamAlert = "alertLevel";
        public string animParamMoving = "isMoving";
        public string animParamHostile = "isHostile";

        [Header("Morale")]
        [Tooltip("Starting morale level. 1 = confident, 0 = broken.")]
        [Range(0f, 1f)] public float startingMorale = 1f;

        [Tooltip("Persist morale between sessions using PlayerPrefs. " +
                 "Uses the GameObject name as key -- make sure names are unique.")]
        public bool persistMorale = false;

        [Header("Events")]
        public UnityEvent onBecameSuspicious;
        public UnityEvent onBecameHostile;
        public UnityEvent onLostTarget;
        public UnityEvent onReturnedToPassive;

        // ---------- Runtime state ---------------------------------------------

        public AlertState CurrentAlertState { get; private set; } = AlertState.Passive;
        public SubState CurrentSubState { get; private set; } = SubState.Idle;
        public SquadRole ActiveRole { get; private set; } = SquadRole.Dynamic;

        public float AwarenessLevel => _sensor != null ? _sensor.AwarenessLevel : 0f;

        /// <summary>
        /// Current floor zone ID. Set automatically by FloorZone trigger volumes.
        /// -1 = no zone registered (on ramp / transitioning / no zones in scene).
        /// </summary>
        public int CurrentFloorID { get; set; } = -1;

        /// <summary>
        /// Current morale level (0-1).
        /// High morale = confident, standard behaviour.
        /// Low morale = cautious or desperate depending on personality.
        /// </summary>
        public float MoraleLevel { get; private set; } = 1f;

        /// <summary>Morale category derived from MoraleLevel.</summary>
        public MoraleState CurrentMorale
        {
            get
            {
                if (MoraleLevel >= 0.7f) return MoraleState.High;
                if (MoraleLevel >= 0.3f) return MoraleState.Medium;
                return MoraleState.Low;
            }
        }

        public Vector3? LastKnownPosition =>
            _hasLastKnown ? _lastKnownPosition : (Vector3?)null;

        // ---------- Internal components ---------------------------------------

        private IStealthMovement _movement;
        private AwarenessSensor _sensor;
        private StealthTarget _target;
        private HideSpotScanner _scanner;

        // ---------- Internal state --------------------------------------------

        private int _patrolIndex;
        private bool _pingPongForward = true;
        private float _waypointWaitTimer;
        private bool _waitingAtWaypoint;

        private float _stateTimer;
        private float _searchTimer;
        private Vector3 _searchCenter;
        private Vector3 _lastKnownPosition;
        private bool _hasLastKnown;


        private ISearchStrategy _searchStrategy;
        private HashSet<int> _visitedCells = new HashSet<int>();
        private float _searchStartTime;
        private Vector3 _currentSearchDest;
        private bool _hasSearchDest;
        private int _searchPassCount;
        private float _lookAroundTimer;
        private Quaternion _lookAroundTarget;
        private bool _hasLookTarget;
        private float _lastPlayerFoundRecordTime = -999f;
        private bool _recordedFlightThisContact;

        private List<Vector3> _guardZonePoints = new List<Vector3>();
        private int _guardZoneIndex;
        private float _guardZoneWaitTimer;
        private bool _guardZoneWaiting;

        private Vector3 _spawnPosition;
        private Quaternion _spawnRotation;
        private Vector3 _lastSeenFlightVector;
        private Vector3 _lastSeenPosition;
        private bool _scanRequested;

        private int _hashAlert;
        private int _hashMoving;
        private int _hashHostile;
        private bool _hasAnimator;

        // Morale
        private int _timesLostTarget;
        private float _passiveTimer;
        private const string MoralePrefsPrefix = "SHA_Morale_";

        // Base values from personality preset -- morale scales from these
        // stored once so inspector fields are never written during play
        private float _baseSuspicionThreshold;
        private float _baseHostileThreshold;
        private float _baseSensorRiseSpeed;
        private float _baseSensorDecaySpeed;
        private float _baseSearchDuration;
        private float _baseAgentSpeed;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            AutoConfigure();
            LoadMorale();
            HuntDirector.RegisterUnit(this);
        }

        private void OnDestroy()
        {
            SaveMorale();
            HuntDirector.UnregisterUnit(this);
        }

        private void Start()
        {
            if (behaviourMode == BehaviourMode.GuardZone && guardZoneWaypointCount > 0)
                GenerateGuardZonePoints();
        }

        private void Update()
        {
            if (_target == null)
            {
                _target = HuntDirector.GetTarget();
                if (_target != null && _sensor != null)
                    _sensor.SetTarget(_target);
                return;
            }

            _stateTimer += Time.deltaTime;

            TickHFSM();
            TickMoraleRecovery();
            UpdateAnimator();
        }

        // ---------- Auto-Configure --------------------------------------------

        private void AutoConfigure()
        {
            // Movement provider -- use assigned or auto-add NavMeshMovement
            if (movementProvider != null && movementProvider is IStealthMovement provider)
            {
                _movement = provider;
            }
            else
            {
                // Auto-configure NavMeshMovement
                var navMove = GetComponent<NavMeshMovement>();
                if (navMove == null)
                {
                    // Ensure NavMeshAgent exists first -- NavMeshMovement needs it
                    if (GetComponent<NavMeshAgent>() == null)
                        gameObject.AddComponent<NavMeshAgent>();

                    navMove = gameObject.AddComponent<NavMeshMovement>();
                }

                _movement = navMove;

                // Keep movementProvider field in sync for inspector visibility
                movementProvider = navMove as MonoBehaviour;
            }

            if (animator == null)
                animator = GetComponentInChildren<Animator>();

            if (animator != null)
            {
                _hasAnimator = true;
                _hashAlert = Animator.StringToHash(animParamAlert);
                _hashMoving = Animator.StringToHash(animParamMoving);
                _hashHostile = Animator.StringToHash(animParamHostile);
            }

            _sensor = GetComponent<AwarenessSensor>();
            if (_sensor == null)
                _sensor = gameObject.AddComponent<AwarenessSensor>();

            // Only overwrite perception ranges -- never touch layer masks.
            // Layer masks are configured manually in the inspector or via
            // the Auto-Configure Layer Masks button -- never overwritten here.
            _sensor.sightRange = sightRange;
            _sensor.sightAngle = sightAngle;
            _sensor.hearingRange = hearingRange;
            _sensor.sightAccumulatorRate = sightDetectionSpeed;
            _sensor.sightDecaySpeed = sightDecaySpeed;

            ApplyPersonalityPreset();
            StoreBaseValues();

            // If no head bone found create a virtual eye position child object
            // so sight raycasts start from the correct height, not from feet
            Transform headBone = FindHeadBone();
            Transform origin;

            if (headBone != null)
            {
                origin = headBone;
            }
            else
            {
                // Reuse existing eye marker or create one
                Transform existing = transform.Find("__SightOrigin__");
                if (existing != null)
                {
                    existing.localPosition = Vector3.up * eyeHeight;
                    origin = existing;
                }
                else
                {
                    var eyeGo = new GameObject("__SightOrigin__");
                    eyeGo.hideFlags = HideFlags.HideInHierarchy;
                    eyeGo.transform.SetParent(transform);
                    eyeGo.transform.localPosition = Vector3.up * eyeHeight;
                    origin = eyeGo.transform;
                }
            }

            _sensor.Configure(origin, null);

            ActiveRole = manualRole != SquadRole.Dynamic ? manualRole : SquadRole.Dynamic;

            // Apply morale modifiers after preset (morale may already be loaded)
            ApplyMoraleModifiers();

            // Scanner -- auto-add, runs as coroutine on demand
            _scanner = GetComponent<HideSpotScanner>();
            if (_scanner == null)
                _scanner = gameObject.AddComponent<HideSpotScanner>();

            // Search strategy -- use override or auto-add ReachabilitySearch
            if (searchStrategyOverride != null
             && searchStrategyOverride is ISearchStrategy overrideStrategy)
            {
                _searchStrategy = overrideStrategy;
            }
            else
            {
                var reachability = GetComponent<ReachabilitySearch>();
                if (reachability == null)
                    reachability = gameObject.AddComponent<ReachabilitySearch>();
                _searchStrategy = reachability;
            }
        }

        private void ApplyPersonalityPreset()
        {
            switch (personality)
            {
                case Personality.Cautious:
                    suspicionThreshold = 0.20f;
                    hostileThreshold = 0.80f;
                    _sensor.riseSpeed = 0.8f;
                    _sensor.decaySpeed = 0.12f;
                    searchDuration = 15f;
                    break;

                case Personality.Balanced:
                    suspicionThreshold = 0.30f;
                    hostileThreshold = 0.70f;
                    _sensor.riseSpeed = 1.2f;
                    _sensor.decaySpeed = 0.10f;
                    searchDuration = 10f;
                    break;

                case Personality.Aggressive:
                    suspicionThreshold = 0.20f;
                    hostileThreshold = 0.50f;
                    _sensor.riseSpeed = 2.0f;
                    _sensor.decaySpeed = 0.08f;
                    searchDuration = 6f;
                    break;
            }
        }

        /// <summary>
        /// Snapshot personality preset values before morale modifies them.
        /// Called once after ApplyPersonalityPreset so morale always scales
        /// from clean base values, never from previously modified ones.
        /// </summary>
        private void StoreBaseValues()
        {
            _baseSuspicionThreshold = suspicionThreshold;
            _baseHostileThreshold = hostileThreshold;
            _baseSensorRiseSpeed = _sensor.riseSpeed;
            _baseSensorDecaySpeed = _sensor.decaySpeed;
            _baseSearchDuration = searchDuration;
            _baseAgentSpeed = GetBaseSpeed();

            // Initialise sensor runtime values from inspector
            _sensor.suspicionThresh = suspicionThreshold;
            _sensor.hostileThresh = hostileThreshold;
            _sensor.searchDur = searchDuration;
        }

        private Transform FindHeadBone()
        {
            if (animator == null) return null;
            Transform head = animator.GetBoneTransform(HumanBodyBones.Head);
            if (head != null) return head;
            return transform.FindDeepChild("head")
                ?? transform.FindDeepChild("Head")
                ?? transform.FindDeepChild("HEAD");
        }

        // ---------- HFSM Core -------------------------------------------------

        private void TickHFSM()
        {
            // Tell sensor current alert state so it can throttle sight checks
            // and scale accumulator rate -- Hostile = instant, Passive = slow
            if (_sensor != null)
            {
                _sensor._isPassive = CurrentAlertState == AlertState.Passive;
                // Scale accumulator by alert state AND current awareness level
                // Higher awareness = more alert = detects faster even when Passive
                float awareBoost = 1f + _sensor.AwarenessLevel * 2f;

                _sensor.sightAccumulatorMultiplier = CurrentAlertState switch
                {
                    AlertState.Hostile => 8f * awareBoost,
                    AlertState.Suspicious => 3f * awareBoost,
                    _ => 1f * awareBoost
                };
            }

            if (_sensor.CanSeeTarget || _sensor.CanHearTarget)
            {
                _lastKnownPosition = _sensor.LastStimulusPosition;
                _hasLastKnown = true;

                if (_target != null && _target.FlightVector.magnitude > 0.1f)
                    _lastSeenFlightVector = _target.FlightVector;

                _lastSeenPosition = _sensor.LastStimulusPosition;
                _scanRequested = false;

                // Record player position for hide spot memory (rate limited)
                if (_sensor.CanSeeTarget
                 && Time.time - _lastPlayerFoundRecordTime > 5f)
                {
                    _lastPlayerFoundRecordTime = Time.time;
                    HuntDirector.RecordPlayerPosition(_sensor.LastStimulusPosition);

                    // Record on nearest PatrolRegion
                    RecordPlayerFoundInRegion(_sensor.LastStimulusPosition);
                }

                _recordedFlightThisContact = false;
            }
            else if (_hasLastKnown && !_recordedFlightThisContact
                  && CurrentAlertState == AlertState.Hostile
                  && _lastSeenFlightVector.magnitude > 0.1f)
            {
                // Just lost contact -- record flight vector for pattern memory
                _recordedFlightThisContact = true;
                HuntDirector.RecordFlightVector(_lastSeenFlightVector);
            }

            float awareness = _sensor.AwarenessLevel;

            // Use sensor runtime thresholds -- morale adjusts these at runtime
            // without touching inspector-visible serialized fields
            float suspThresh = _sensor != null ? _sensor.suspicionThresh : suspicionThreshold;
            float hostThresh = _sensor != null ? _sensor.hostileThresh : hostileThreshold;

            switch (CurrentAlertState)
            {
                case AlertState.Passive:
                    if (awareness >= suspThresh)
                        TransitionTo(AlertState.Suspicious, SubState.Alerted);
                    break;

                case AlertState.Suspicious:
                    if (awareness >= hostThresh)
                        TransitionTo(AlertState.Hostile, SubState.Pursuing);
                    else if (awareness <= 0.05f)
                        TransitionTo(AlertState.Passive, SubState.Returning);
                    break;

                case AlertState.Hostile:
                    if (awareness <= suspThresh)
                    {
                        if (CurrentSubState != SubState.LostTarget)
                            TransitionTo(AlertState.Hostile, SubState.LostTarget);
                    }
                    break;
            }

            switch (CurrentSubState)
            {
                case SubState.Idle: TickIdle(); break;
                case SubState.Patrolling: TickPatrolling(); break;
                case SubState.GuardZonePatrol: TickGuardZonePatrol(); break;
                case SubState.Returning: TickReturning(); break;
                case SubState.Alerted: TickAlerted(); break;
                case SubState.Investigating: TickInvestigating(); break;
                case SubState.Searching: TickSearching(); break;
                case SubState.Pursuing: TickPursuing(); break;
                case SubState.Flanking: TickFlanking(); break;
                case SubState.LostTarget: TickLostTarget(); break;
            }
        }

        // ---------- Transitions -----------------------------------------------

        private void TransitionTo(AlertState newAlert, SubState newSub)
        {
            AlertState prev = CurrentAlertState;
            CurrentAlertState = newAlert;
            CurrentSubState = newSub;
            _stateTimer = 0f;
            _searchTimer = 0f;
            _hasLookTarget = false;
            _lookAroundTimer = 0f;
            _hasPendingNudge = false;

            // Morale events
            if (prev == AlertState.Hostile && newAlert == AlertState.Passive)
            {
                // Lost the target completely -- dock morale
                _timesLostTarget++;
                ModifyMorale(-0.15f);
            }

            if (prev != newAlert)
            {
                switch (newAlert)
                {
                    case AlertState.Suspicious: onBecameSuspicious.Invoke(); break;
                    case AlertState.Hostile: onBecameHostile.Invoke(); break;
                    case AlertState.Passive:
                        onReturnedToPassive.Invoke();
                        _visitedCells.Clear();
                        _searchPassCount = 0;
                        _recordedFlightThisContact = false;
                        _lastPlayerFoundRecordTime = -999f;
                        _searchStrategy?.Reset();
                        HuntDirector.GetSquadBlackboard(squadID)
                            ?.UnregisterSearchUnit(this);
                        break;
                }
            }

            HuntDirector.ReportStateChange(this, newAlert, newSub);
        }

        private void TransitionSubState(SubState newSub)
        {
            CurrentSubState = newSub;
            _stateTimer = 0f;
        }

        // ---------- Movement --------------------------------------------------

        private void MoveTo(Vector3 position)
        {
            if (_movement != null && _movement.IsOnSurface)
                _movement.MoveTo(position);
        }

        private void StopMoving()
        {
            if (_movement != null && _movement.HasPath)
                _movement.Stop();
        }

        private void RecordPlayerFoundInRegion(Vector3 position)
        {
            PatrolRegion best = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < PatrolRegion.All.Count; i++)
            {
                var r = PatrolRegion.All[i];
                if (r.IsAutoGenerated) continue;
                float d = Vector3.Distance(position, r.transform.position);
                if (d < r.radius && d < bestDist) { bestDist = d; best = r; }
            }

            best?.RecordPlayerFound();
        }

        private void RecordSearchedRegion(Vector3 position)
        {
            for (int i = 0; i < PatrolRegion.All.Count; i++)
            {
                var r = PatrolRegion.All[i];
                if (r.IsAutoGenerated) continue;
                if (Vector3.Distance(position, r.transform.position) < r.radius)
                    r.RecordSearch();
            }
        }

        // ---------- Animator --------------------------------------------------

        private void UpdateAnimator()
        {
            if (!_hasAnimator) return;

            bool moving = _movement != null && _movement.Speed > 0.1f && _movement.HasPath;

            SafeSetFloat(_hashAlert, AwarenessLevel);
            SafeSetBool(_hashMoving, moving);
            SafeSetBool(_hashHostile, CurrentAlertState == AlertState.Hostile);
        }

        private void SafeSetFloat(int hash, float value)
        {
            try { animator.SetFloat(hash, value); } catch { }
        }

        private void SafeSetBool(int hash, bool value)
        {
            try { animator.SetBool(hash, value); } catch { }
        }

        // ---------- Public API ------------------------------------------------

        public void AssignRole(SquadRole role)
        {
            if (manualRole != SquadRole.Dynamic) return;
            ActiveRole = role;
        }

        /// <summary>
        /// Called by HuntDirector when the scene alert level changes.
        /// Applies a multiplier on top of the current morale-adjusted rise speed
        /// and optionally scales movement speed.
        /// </summary>
        public void ApplyAlertLevelEffects(float riseMultiplier, float speedMultiplier)
        {
            if (_sensor != null)
                _sensor.riseSpeed = _baseSensorRiseSpeed * riseMultiplier;

            if (_movement != null && _movement.CanOverrideSpeed)
            {
                float baseSpd = _baseAgentSpeed > 0f ? _baseAgentSpeed : GetBaseSpeed();
                _movement.Speed = baseSpd * speedMultiplier;
            }
        }

        public void SetFlankDestination(Vector3 position)
        {
            TransitionSubState(SubState.Flanking);
            MoveTo(position);
        }

        public void ReceiveSquadIntel(Vector3 reportedPosition, float confidence)
        {
            // Raise awareness but set an UNCERTAIN position -- not the exact reported one.
            // Add noise proportional to inverse confidence so low-confidence intel
            // gives a very imprecise search area. This prevents units from homing in
            // on the player via intel they never personally observed.
            if (_sensor == null) return;

            float amount = confidence * 0.3f;
            _sensor.AddAwareness(amount);

            // Only update stimulus position if this intel is better than current
            // and add significant positional noise to prevent heat-seeking
            if (confidence > _sensor.StimulusConfidence * 0.5f)
            {
                float noiseRadius = Mathf.Lerp(12f, 2f, confidence);
                Vector3 noise = Random.insideUnitSphere * noiseRadius;
                noise.y = 0f;

                _sensor.SetIntelPosition(reportedPosition + noise,
                                          confidence * 0.4f);
            }
        }

        public void ForceAlert(Vector3 position, float confidence = 0.8f)
        {
            _sensor?.RaiseAwareness(confidence, position, confidence);
            _lastKnownPosition = position;
            _hasLastKnown = true;
        }

        public void ReceiveTarget(StealthTarget target)
        {
            _target = target;
            _sensor?.SetTarget(target);
        }

        public void AssignSquadID(int id)
        {
            squadID = id;
        }

        // Pending nudge -- applied when unit finishes current waypoint
        private Vector3 _pendingNudge;
        private bool _hasPendingNudge;

        public void SetNudgeDestination(Vector3 position)
        {
            if (CurrentAlertState != AlertState.Passive) return;

            // Ignore nudge if already close to destination
            if (Vector3.Distance(transform.position, position) < 5f) return;

            // GuardZone units only accept nudges within their zone
            if (behaviourMode == BehaviourMode.GuardZone)
            {
                Vector3 center = guardZoneCenter != null
                    ? guardZoneCenter.position
                    : _spawnPosition;

                if (Vector3.Distance(position, center) > guardZoneRadius * 1.5f)
                    return;
            }

            // Store as pending -- applied when unit reaches current waypoint
            // This prevents interrupting mid-patrol which causes looping behavior
            _pendingNudge = position;
            _hasPendingNudge = true;
        }

        /// <summary>Called by patrol ticks when a waypoint is reached.</summary>
        private bool ConsumePendingNudge()
        {
            if (!_hasPendingNudge) return false;
            _hasPendingNudge = false;
            MoveTo(_pendingNudge);
            return true;
        }

        // ---------- Gizmos ----------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            // Guard zone
            if (behaviourMode == BehaviourMode.GuardZone)
            {
                Vector3 center = guardZoneCenter != null
                    ? guardZoneCenter.position
                    : transform.position;

                Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.15f);
                Gizmos.DrawWireSphere(center, guardZoneRadius);
                Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.04f);
                Gizmos.DrawSphere(center, guardZoneRadius);

                if (_guardZonePoints.Count > 0)
                {
                    Gizmos.color = new Color(0.2f, 0.8f, 1f, 0.6f);
                    for (int i = 0; i < _guardZonePoints.Count; i++)
                    {
                        Gizmos.DrawWireSphere(_guardZonePoints[i], 0.25f);
                        int next = (i + 1) % _guardZonePoints.Count;
                        Gizmos.DrawLine(_guardZonePoints[i], _guardZonePoints[next]);
                    }
                }
            }

            // Patrol route
            if (behaviourMode == BehaviourMode.Patrol &&
                patrolPoints != null && patrolPoints.Length > 1)
            {
                Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.8f);
                for (int i = 0; i < patrolPoints.Length; i++)
                {
                    if (patrolPoints[i] == null) continue;
                    Gizmos.DrawWireSphere(patrolPoints[i].position, 0.3f);

                    if (patrolPattern == PatrolPattern.Loop)
                    {
                        int next = (i + 1) % patrolPoints.Length;
                        if (patrolPoints[next] != null)
                            Gizmos.DrawLine(patrolPoints[i].position,
                                            patrolPoints[next].position);
                    }
                    else if (i + 1 < patrolPoints.Length && patrolPoints[i + 1] != null)
                    {
                        Gizmos.DrawLine(patrolPoints[i].position,
                                        patrolPoints[i + 1].position);
                    }
                }
            }

            if (!Application.isPlaying) return;

            if (_hasLastKnown)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawWireCube(_lastKnownPosition + Vector3.up * 0.5f, Vector3.one * 0.4f);
                Gizmos.DrawLine(transform.position, _lastKnownPosition);
            }

            Gizmos.color = CurrentAlertState switch
            {
                AlertState.Passive => Color.green,
                AlertState.Suspicious => Color.yellow,
                AlertState.Hostile => Color.red,
                _ => Color.white
            };
            Gizmos.DrawWireSphere(transform.position + Vector3.up * 2.8f, 0.2f);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!Application.isPlaying) return;

            UnityEditor.Handles.Label(
                transform.position + Vector3.up * 3.2f,
                CurrentAlertState + " / " + CurrentSubState + "\n" +
                "Awareness: " + AwarenessLevel.ToString("F2") +
                "  Role: " + ActiveRole);
        }
#endif
    }

    // ---------- Transform extension -------------------------------------------

    public static class TransformExtensions
    {
        public static Transform FindDeepChild(this Transform parent, string name)
        {
            foreach (Transform child in parent)
            {
                if (child.name == name) return child;
                Transform result = child.FindDeepChild(name);
                if (result != null) return result;
            }
            return null;
        }
    }
}