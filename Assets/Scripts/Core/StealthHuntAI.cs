using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace StealthHuntAI
{
    /// <summary>Logical trigger that determines when this clip plays.</summary>
    public enum AnimTrigger
    {
        Idle,           // Passive, standing still
        Walk,           // Passive, moving
        Return,         // Passive, returning to spawn/patrol
        Alerted,        // Suspicious, stopping to look
        Investigate,    // Suspicious, moving toward last known position
        Search,         // Suspicious, searching area
        Pursuing,       // Hostile, chasing player
        Shooting,       // Hostile, stopped to engage
        LostTarget,     // Hostile, lost player
        Death,          // On death
        Custom          // Manual only -- call PlayAnimState("name") from code
    }

    /// <summary>
    /// Maps an AnimTrigger to one or more Animator clip names.
    /// When multiple clips are assigned, one is chosen at random each time the state is entered.
    /// </summary>
    [System.Serializable]
    public class AnimSlot
    {
        public AnimTrigger trigger = AnimTrigger.Idle;
        public List<string> clips = new List<string>();

        [Tooltip("Only used when trigger is Custom. Name used with PlayAnimState().")]
        public string customName = "";

        /// <summary>Pick a random clip from the list. Returns null if empty.</summary>
        public string Pick()
        {
            if (clips == null || clips.Count == 0) return null;
            if (clips.Count == 1) return string.IsNullOrEmpty(clips[0]) ? null : clips[0];
            // Filter out empty entries
            var valid = clips.FindAll(c => !string.IsNullOrEmpty(c));
            if (valid.Count == 0) return null;
            return valid[UnityEngine.Random.Range(0, valid.Count)];
        }
    }

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
        Shooting,
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

        [Header("Preset")]
        [Tooltip("Drag a StealthAIPreset asset here and click Apply Preset " +
                 "to configure all settings at once.")]
        public StealthAIPreset preset;

        [Header("Perception")]
        [Range(1f, 60f)] public float sightRange = 15f;
        [Range(10f, 360f)] public float sightAngle = 90f;
        [Range(1f, 40f)] public float hearingRange = 20f;

        [Tooltip("How fast sight exposure builds up. " +
                 "Higher = faster detection. 1.5 = ~0.67s at full visibility. " +
                 "Hostile state uses 8x this, Suspicious 3x.")]
        [Range(0.1f, 10f)] public float sightDetectionSpeed = 1.5f;

        [Tooltip("How fast sight exposure drains when target leaves FOV. "
                 + "Lower = exposure persists longer after brief cover.")]
        [Range(0.05f, 3f)] public float sightDecaySpeed = 0.3f;

        [Header("Awareness Thresholds")]
        [Range(0f, 1f)] public float suspicionThreshold = 0.25f;
        [Range(0f, 1f)] public float hostileThreshold = 0.80f;
        [Tooltip("Minimum seconds guard stays suspicious before returning to passive.")]
        [Range(0f, 30f)] public float suspiciousDwellTime = 12f;

        [Header("Behaviour")]
        public Personality personality = Personality.Balanced;
        public BehaviourMode behaviourMode = BehaviourMode.Idle;

        [Tooltip("Speed multiplier when patrolling or guarding. " +
                 "Applied on top of base speed. 0.5 = half speed patrol.")]
        [Range(0.1f, 1f)] public float patrolSpeedMultiplier = 0.65f;

        [Tooltip("Speed multiplier when actively pursuing or searching hostile.")]
        [Range(0.5f, 2f)] public float chaseSpeedMultiplier = 1.4f;

        [Tooltip("Patrol waypoints. Leave empty to auto-generate patrol around spawn position.")]
        public Transform[] patrolPoints;

        [Tooltip("Radius for auto-generated patrol when no patrol points are assigned.")]
        [Range(2f, 30f)] public float autoPatrolRadius = 8f;

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

        [Header("Animation")]
        public Animator animator;

        [Tooltip("Crossfade transition duration between animation states.")]
        [Range(0f, 0.5f)] public float animTransitionDuration = 0.15f;

        [Header("Aiming IK")]
        [Tooltip("Enable upper body IK aiming toward last known position. " +
                 "Requires IK Pass enabled on Animator Base Layer.")]
        public bool enableAimIK = true;

        [Tooltip("How much the body rotates toward aim target.")]
        [Range(0f, 1f)] public float aimBodyWeight = 0.3f;

        [Tooltip("How much the head rotates toward aim target.")]
        [Range(0f, 1f)] public float aimHeadWeight = 0.7f;

        [Tooltip("How much the eyes rotate toward aim target.")]
        [Range(0f, 1f)] public float aimEyesWeight = 0f;

        [Tooltip("Height offset applied to aim target.")]
        [Range(0f, 2f)] public float aimTargetHeightOffset = 0.8f;

        [Tooltip("Map state names to animation clips. Add/remove as needed.")]
        public List<AnimSlot> animSlots = new List<AnimSlot>();

        [Header("Squad Alert")]
        [Tooltip("When this unit becomes Hostile, all units within this radius are alerted too.")]
        [Range(0f, 80f)] public float alertPropagationRadius = 30f;

        [Header("Combat (optional -- requires Combat Pack)")]
        [Tooltip("Assign a MonoBehaviour implementing ICombatBehaviour to override " +
                 "default Hostile behaviour with a custom combat system.")]
        public MonoBehaviour combatBehaviourOverride;

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
        /// <summary>True if this unit is dead. Set by health system via SetDead().</summary>
        public bool IsDead { get; private set; }
        public void SetDead() => IsDead = true;

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
        private readonly TacticalPatrolController _tacticalPatrol
            = new TacticalPatrolController();

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
        private string _currentAnimState = "";
        private float _aimWeight;
        private ICombatBehaviour _combat;
        private bool _wasInCombat;
        private bool _suppressAlertPropagation;
        private float _suspiciousDwellTimer;
        private float _rotationVelocity;
        private float _currentYaw;
        private float _stuckTimer;
        private Vector3 _lastStuckCheckPos;
        private float _deadBodyScanTimer;
        private const float DeadBodyScanInterval = 0.8f;
        private float _lastSeenTimer;      // time since last saw player
        private const float CombatMemoryTime = 8f; // stay hostile this long after losing sight
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
        private NavMeshAgent _agent;
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

        // Called by Unity when component is first added in editor
        private void Reset()
        {
            EnsureDefaultAnimAssignments();
        }

        private void Awake()
        {
            _spawnPosition = transform.position;
            _spawnRotation = transform.rotation;
            _agent = GetComponent<NavMeshAgent>();
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
            TickDeadBodyDetection();
            UpdateAnimator();
        }

        /// <summary>
        /// Scan for dead squad members in sight range.
        /// Finding a dead body instantly triggers Suspicious or Hostile
        /// depending on current alert state.
        /// </summary>
        private void TickDeadBodyDetection()
        {
            if (IsDead || CurrentAlertState == AlertState.Hostile) return;

            _deadBodyScanTimer += Time.deltaTime;
            if (_deadBodyScanTimer < DeadBodyScanInterval) return;
            _deadBodyScanTimer = 0f;

            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                if (unit == null || unit == this) continue;
                if (!unit.IsDead) continue;

                float dist = Vector3.Distance(transform.position, unit.transform.position);
                if (dist > sightRange) continue;

                // Check LOS to dead body
                Vector3 dir = unit.transform.position - transform.position;
                if (Physics.Raycast(transform.position + Vector3.up * 1.6f,
                    dir.normalized, dir.magnitude, LayerMask.GetMask("Default", "Environment")))
                    continue; // blocked

                // Found dead body -- react based on current state
                if (CurrentAlertState == AlertState.Passive)
                {
                    // Jump straight to Suspicious with high awareness
                    if (_sensor != null)
                        _sensor.AwarenessLevel = Mathf.Max(
                            _sensor.AwarenessLevel, suspicionThreshold + 0.15f);
                    HuntDirector.BroadcastSound(
                        unit.transform.position, 0.6f, 20f);
                }
                else if (CurrentAlertState == AlertState.Suspicious)
                {
                    // Go Hostile -- dead body while suspicious = danger
                    ForceHostile();
                    // Share dead body position as threat intel
                    var board = SquadBlackboard.Get(squadID);
                    board?.ShareIntel(unit.transform.position, 0.35f);
                }
                break; // one dead body is enough
            }
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

            // Tune NavMeshAgent for smooth organic movement
            if (_agent != null)
            {
                _agent.acceleration = 12f;
                _agent.angularSpeed = 200f;
                _agent.autoBraking = true;
                // Minimal radius -- guards pass freely in narrow corridors
                _agent.radius = 0.15f;
                // No avoidance -- GOAP spread destinations handle separation
                // Avoidance causes deadlocks in tight spaces
                _agent.obstacleAvoidanceType = UnityEngine.AI.ObstacleAvoidanceType.NoObstacleAvoidance;
                _agent.avoidancePriority = 50;
                // Enable stair/ramp traversal -- required for multi-floor navigation
                _agent.autoTraverseOffMeshLink = true;
                _agent.baseOffset = Mathf.Max(_agent.baseOffset, 0f);
            }

            if (animator != null)
            {
                _hasAnimator = true;
                // CrossFade system -- no parameter hashing needed
            }

            // Init ragdoll -- all rigidbodies start kinematic so animation controls movement
            var ragdollRbs = GetComponentsInChildren<Rigidbody>();
            foreach (var rb in ragdollRbs)
            {
                rb.isKinematic = true;
                rb.useGravity = false;
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
                    suspicionThreshold = 0.15f;
                    hostileThreshold = 0.75f;
                    _sensor.riseSpeed = 0.8f;
                    _sensor.decaySpeed = 0.12f;
                    searchDuration = 15f;
                    break;

                case Personality.Balanced:
                    suspicionThreshold = 0.25f;
                    hostileThreshold = 0.80f;
                    _sensor.riseSpeed = 1.2f;
                    _sensor.decaySpeed = 0.10f;
                    searchDuration = 10f;
                    break;

                case Personality.Aggressive:
                    suspicionThreshold = 0.20f;
                    hostileThreshold = 0.60f;
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
            _currentYaw = transform.eulerAngles.y;

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
                    {
                        _suspiciousDwellTimer = 0f;
                        TransitionTo(AlertState.Hostile, SubState.Pursuing);
                    }
                    else if (awareness <= 0.05f)
                    {
                        // Must stay suspicious for minimum dwell time before returning to passive
                        _suspiciousDwellTimer += Time.deltaTime;
                        if (_suspiciousDwellTimer >= suspiciousDwellTime)
                        {
                            _suspiciousDwellTimer = 0f;
                            TransitionTo(AlertState.Passive, SubState.Returning);
                        }
                    }
                    else
                    {
                        _suspiciousDwellTimer = 0f;
                    }
                    break;

                case AlertState.Hostile:
                    // Track how long since we last saw player
                    if (_sensor != null && _sensor.CanSeeTarget)
                        _lastSeenTimer = 0f;
                    else
                        _lastSeenTimer += Time.deltaTime;

                    // Only go to LostTarget after combat memory expires
                    // Guards remember where player was for CombatMemoryTime seconds
                    if (_lastSeenTimer >= CombatMemoryTime && awareness <= suspThresh)
                    {
                        if (CurrentSubState != SubState.LostTarget)
                            TransitionTo(AlertState.Hostile, SubState.LostTarget);
                    }
                    // Keep pursuing last known position even without sight
                    else if (!_sensor.CanSeeTarget
                          && CurrentSubState == SubState.Pursuing
                          && _hasLastKnown)
                    {
                        // Continue pursuing last known -- dont switch to LostTarget yet
                    }
                    break;
            }

            // Re-cache combat if not set yet (handles runtime assignment)
            if (_combat == null && combatBehaviourOverride != null)
                _combat = combatBehaviourOverride as ICombatBehaviour;

            // Combat Pack handoff -- runs before SubState switch
            if (CurrentAlertState == AlertState.Hostile && _combat != null)
            {
                if (!_wasInCombat || !_combat.WantsControl)
                {
                    _wasInCombat = true;
                    _combat.OnEnterCombat(this);
                }
                if (_combat.WantsControl)
                {
                    _combat.Tick(this);
                    return;
                }
                // Core is searching -- shoot immediately on direct LOS
                if (_target != null)
                {
                    Vector3 toPlayer = _target.Position - transform.position;
                    bool hasDirectLOS = !Physics.Raycast(
                        transform.position + Vector3.up * 1.6f,
                        toPlayer.normalized,
                        toPlayer.magnitude - 0.5f,
                        LayerMask.GetMask("Default", "Environment"));

                    if (hasDirectLOS)
                    {
                        // Face toward player
                        CombatFaceToward(_target.Position, 400f);
                        GetComponent<IShootable>()?.TryShoot(_target.Position);
                        // Regain combat control
                        _combat.OnEnterCombat(this);
                        return;
                    }
                }
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
                case SubState.Shooting: TickShooting(); break;
                case SubState.LostTarget: TickLostTarget(); break;
            }
        }

        // ---------- Transitions -----------------------------------------------

        private void TransitionTo(AlertState newAlert, SubState newSub)
        {
            AlertState prev = CurrentAlertState;

            // Notify Combat Pack when leaving Hostile
            if (prev == AlertState.Hostile && newAlert != AlertState.Hostile
             && _combat != null && _wasInCombat)
            {
                _wasInCombat = false;
                _combat.OnExitCombat(this);
            }

            // Alert squad when becoming Hostile -- propagate to nearby units
            if (newAlert == AlertState.Hostile && prev != AlertState.Hostile
             && !_suppressAlertPropagation)
            {
                _lastSeenTimer = 0f;
                HuntDirector.AlertSquad(this, alertPropagationRadius);
            }

            CurrentAlertState = newAlert;
            CurrentSubState = newSub;
            if (_sensor != null)
                _sensor.IsHostile = (newAlert == AlertState.Hostile);
            _stateTimer = 0f;
            _searchTimer = 0f;
            _hasLookTarget = false;
            if (_agent != null) _agent.updateRotation = true;
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
            // Always re-enable agent rotation on state change
            if (_agent != null) _agent.updateRotation = true;
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
            // Skip when Combat Pack owns animation
            if (_combat != null && _combat.WantsControl) return;

            float velocity = _movement != null ? _movement.ActualSpeed : 0f;
            bool moving = velocity > 0.1f;

            string target = GetTargetAnimState(moving);
            if (string.IsNullOrEmpty(target)) return;

            // Transition to new state
            if (target != _currentAnimState)
            {
                _currentAnimState = target;
                try { animator.CrossFade(target, animTransitionDuration); } catch { }
                return;
            }

            // Re-trigger if animation has finished playing (not looping clips)
            // Normalised time >= 0.95 means clip is near end
            var info = animator.GetCurrentAnimatorStateInfo(0);
            if (info.normalizedTime >= 0.95f && !info.loop)
                try { animator.CrossFade(target, 0f); } catch { }
        }

        private string GetTargetAnimState(bool moving)
        {
            AnimTrigger trigger;

            switch (CurrentAlertState)
            {
                case AlertState.Hostile:
                    if (CurrentSubState == SubState.Shooting)
                        trigger = AnimTrigger.Shooting;
                    else if (CurrentSubState == SubState.Pursuing
                          || CurrentSubState == SubState.Flanking)
                        trigger = AnimTrigger.Pursuing;
                    else
                        trigger = AnimTrigger.LostTarget;
                    break;

                case AlertState.Suspicious:
                    if (CurrentSubState == SubState.Investigating)
                        trigger = AnimTrigger.Investigate;
                    else if (CurrentSubState == SubState.Searching)
                        trigger = AnimTrigger.Search;
                    else
                        trigger = AnimTrigger.Alerted;
                    break;

                default: // Passive
                    if (CurrentSubState == SubState.Returning)
                        trigger = AnimTrigger.Return;
                    else if (moving)
                        trigger = AnimTrigger.Walk;
                    else
                        trigger = AnimTrigger.Idle;
                    break;
            }

            return GetClip(trigger) ?? "";
        }

        /// <summary>Returns a randomly picked clip for a Custom slot by customName.</summary>
        public string GetCustomClip(string customName)
        {
            for (int i = 0; i < animSlots.Count; i++)
            {
                var slot = animSlots[i];
                if (slot.trigger == AnimTrigger.Custom && slot.customName == customName)
                    return slot.Pick();
            }
            return null;
        }

        /// <summary>Returns a randomly picked clip for a given trigger. Returns null if not assigned.</summary>
        public string GetClip(AnimTrigger trigger)
        {
            for (int i = 0; i < animSlots.Count; i++)
            {
                var slot = animSlots[i];
                if (slot.trigger == trigger)
                    return slot.Pick();
            }
            return null;
        }

        /// <summary>
        /// Play a Custom slot by its customName. Picks randomly if multiple clips assigned.
        /// Example: ai.PlayAnimState("HitReaction")
        /// </summary>
        public void PlayAnimState(string customName, float transitionDuration = -1f)
        {
            if (!_hasAnimator) return;
            string clip = GetCustomClip(customName);
            if (string.IsNullOrEmpty(clip)) return;
            float dur = transitionDuration >= 0f ? transitionDuration : animTransitionDuration;
            try { animator.CrossFade(clip, dur); } catch { }
        }

        private void OnAnimatorIK(int layer)
        {
            if (!_hasAnimator || !enableAimIK) return;
            if (CurrentAlertState == AlertState.Passive) return;

            // Determine aim target
            Vector3 aimPos;
            if (_sensor != null && _sensor.CanSeeTarget && _target != null)
                aimPos = _target.Position + Vector3.up * aimTargetHeightOffset;
            else if (_hasLastKnown)
                aimPos = _lastKnownPosition + Vector3.up * aimTargetHeightOffset;
            else
                aimPos = transform.position + transform.forward * 5f
                       + Vector3.up * aimTargetHeightOffset;

            // Weight based on alert state
            float weight = CurrentAlertState switch
            {
                AlertState.Hostile => 1.0f,
                AlertState.Suspicious => 0.4f,
                _ => 0f
            };

            // Smooth weight transition
            _aimWeight = Mathf.MoveTowards(_aimWeight, weight, 2f * Time.deltaTime);
            float smoothWeight = _aimWeight;

            animator.SetLookAtWeight(smoothWeight,
                                     aimBodyWeight * smoothWeight,
                                     aimHeadWeight * smoothWeight,
                                     aimEyesWeight * smoothWeight,
                                     0.5f);
            animator.SetLookAtPosition(aimPos);
        }

        /// <summary>Kill this unit externally. Called by health components.</summary>
        public void Die()
        {
            if (!enabled && !(_agent != null && _agent.enabled)) return; // already dead

            // Stop and fully remove from NavMesh
            if (_agent != null)
            {
                _agent.isStopped = true;
                _agent.velocity = Vector3.zero;
                _agent.ResetPath();
                _agent.enabled = false;
            }
            // Disable CharacterController if present -- stops any position updates
            var cc = GetComponent<UnityEngine.CharacterController>();
            if (cc != null) cc.enabled = false;

            // Ragdoll -- disable animator and enable physics on all rigidbodies
            bool hasRagdoll = false;
            var rbs = GetComponentsInChildren<Rigidbody>();
            foreach (var rb in rbs)
            {
                rb.isKinematic = false;
                rb.useGravity = true;
                hasRagdoll = true;
            }

            if (hasRagdoll)
            {
                // Disable animator so ragdoll physics takes over
                if (_hasAnimator) animator.enabled = false;
            }
            else
            {
                // No ragdoll -- play death animation and disable colliders
                PlayDeathAnim();
                var cols = GetComponentsInChildren<Collider>();
                foreach (var col in cols) col.enabled = false;
            }

            enabled = false;
        }

        public void PlayDeathAnim()
        {
            if (!_hasAnimator) return;
            string clip = GetClip(AnimTrigger.Death);
            if (string.IsNullOrEmpty(clip)) return;
            try { animator.CrossFade(clip, 0.1f); } catch { }
        }

        /// <summary>Ensure default AnimSlots exist on first setup.</summary>
        private void EnsureDefaultAnimAssignments()
        {
            EnsureDefaultAnimAssignmentsPublic();
        }

        /// <summary>Public version called by editor Auto Assign.</summary>
        public void EnsureDefaultAnimAssignmentsPublic()
        {
            var defaults = new[]
            {
                AnimTrigger.Idle, AnimTrigger.Walk, AnimTrigger.Return,
                AnimTrigger.Alerted, AnimTrigger.Investigate, AnimTrigger.Search,
                AnimTrigger.Pursuing, AnimTrigger.Shooting, AnimTrigger.LostTarget,
                AnimTrigger.Death
            };

            foreach (var t in defaults)
            {
                bool exists = false;
                for (int i = 0; i < animSlots.Count; i++)
                    if (animSlots[i].trigger == t) { exists = true; break; }
                if (!exists)
                    animSlots.Add(new AnimSlot { trigger = t, clips = new List<string>() });
            }
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>Returns the current StealthTarget (player). Null if not found.</summary>
        public StealthTarget GetTarget() => _target;

        /// <summary>Force this unit into Hostile state -- also alerts nearby squad.</summary>
        public void ForceHostile()
        {
            if (_sensor != null)
                _sensor.AwarenessLevel = hostileThreshold + 0.1f;
            if (CurrentAlertState != AlertState.Hostile)
                TransitionTo(AlertState.Hostile, SubState.Pursuing);
        }

        /// <summary>Force Hostile without triggering AlertSquad -- used by AlertSquad itself.</summary>
        public void ForceHostileSilent()
        {
            if (_sensor != null)
                _sensor.AwarenessLevel = hostileThreshold + 0.1f;
            if (CurrentAlertState != AlertState.Hostile)
            {
                // Set flag to suppress AlertSquad call in TransitionTo
                _suppressAlertPropagation = true;
                TransitionTo(AlertState.Hostile, SubState.Pursuing);
                _suppressAlertPropagation = false;
            }
        }

        /// <summary>Add suppression to this unit via ISuppressionHandler component.</summary>
        public void AddSuppression(float amount)
        {
            GetComponent<ISuppressionHandler>()?.AddSuppression(amount);
        }

        /// <summary>True when this unit is suppressed by player fire.</summary>
        public bool IsSuppressed
            => GetComponent<ISuppressionHandler>()?.IsSuppressed ?? false;

        /// <summary>Returns the AwarenessSensor component.</summary>
        public AwarenessSensor Sensor => _sensor;

        /// <summary>Move toward a world position. For use by Combat Pack.</summary>
        public void CombatMoveTo(Vector3 pos, float speedMultiplier = 1f)
        {
            if (_agent == null) return;
            _agent.isStopped = false;
            _agent.stoppingDistance = 0.1f;
            float baseSpeed = _baseAgentSpeed > 0.1f ? _baseAgentSpeed : _agent.speed;
            float chase = chaseSpeedMultiplier > 0.1f ? chaseSpeedMultiplier : 1f;
            _agent.speed = Mathf.Max(0.5f, baseSpeed * chase * speedMultiplier);
            // Sample onto NavMesh -- small radius prevents wall snap on ramps
            UnityEngine.AI.NavMeshHit hit;
            if (UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, 1.5f,
                UnityEngine.AI.NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else if (UnityEngine.AI.NavMesh.SamplePosition(pos, out hit, 4f,
                UnityEngine.AI.NavMesh.AllAreas))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(pos);
        }

        /// <summary>Stop movement. For use by Combat Pack.</summary>
        public void CombatStop() => StopMoving();

        /// <summary>Move to position. For use by TacticalPatrolController.</summary>
        public void PatrolMoveTo(Vector3 pos) => MoveTo(pos);

        /// <summary>Stop movement. For use by TacticalPatrolController.</summary>
        public void PatrolStop() => StopMoving();

        /// <summary>Face toward a world position smoothly. For use by Combat Pack.</summary>
        public void CombatFaceToward(Vector3 pos, float speed = 180f)
        {
            Vector3 dir = (pos - transform.position);
            dir.y = 0f;
            if (dir.magnitude < 0.1f) return;

            if (_agent != null && _agent.updateRotation)
                _agent.updateRotation = false;

            // Target angle
            float targetYaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            // Smooth with inertia -- SmoothDampAngle prevents jitter
            float smoothTime = 600f / Mathf.Max(1f, speed); // fast speed = short smooth time
            _currentYaw = Mathf.SmoothDampAngle(
                _currentYaw, targetYaw,
                ref _rotationVelocity,
                smoothTime * Time.deltaTime * 8f,
                speed);

            transform.rotation = Quaternion.Euler(0f, _currentYaw, 0f);
        }

        /// <summary>Re-enable agent rotation. Call after CombatFaceToward when done.</summary>
        public void CombatRestoreRotation()
        {
            if (_agent != null) _agent.updateRotation = true;
        }

        /// <summary>Transition to a SubState. For use by Combat Pack.</summary>
        public void CombatSetSubState(SubState state) => TransitionSubState(state);

        /// <summary>Play an AnimSlot trigger. For use by Combat Pack.</summary>
        public void CombatPlayAnim(AnimTrigger trigger, float duration = -1f)
        {
            if (!_hasAnimator) return;
            string clip = GetClip(trigger);
            if (string.IsNullOrEmpty(clip)) return;
            float dur = duration >= 0f ? duration : animTransitionDuration;
            try { animator.CrossFade(clip, dur); } catch { }
        }

        /// <summary>Apply the assigned preset to this unit and its AwarenessSensor.</summary>
        public void ApplyPreset()
        {
            if (preset == null) return;
            preset.ApplyTo(this);
        }

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