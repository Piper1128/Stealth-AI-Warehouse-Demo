using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    [DefaultExecutionOrder(-10)]  // Run before StealthHuntAI (0)
    /// <summary>
    /// Perception component for StealthHuntAI units.
    /// Handles sight, hearing and awareness using a unified exposure model.
    /// Auto-added by StealthHuntAI -- do not add manually.
    /// </summary>
    public class AwarenessSensor : MonoBehaviour
    {
        // ---------- Inspector -- Sight ----------------------------------------

        [Header("Sight")]
        [Range(1f, 60f)] public float sightRange = 15f;
        [Range(10f, 360f)] public float sightAngle = 90f;
        [Range(1f, 30f)] public float peripheralRange = 5f;

        [Tooltip("Number of raycast points per sight check. More = better partial cover.")]
        [Range(1, 5)] public int sightCheckPoints = 3;

        [Tooltip("Horizontal spread of multi-point sight check in world units.")]
        [Range(0.1f, 1f)] public float sightCheckSpread = 0.4f;

        [Tooltip("If true, FOV cone follows the head bone rotation from animation. " +
                 "If false, FOV uses root transform forward (NavMeshAgent rotation).")]
        public bool sightFollowHeadBone = true;

        [Header("Sight Detection")]
        [Tooltip("Seconds of delay before awareness begins rising after first sighting. " +
                 "Simulates reaction time -- player has a brief window to hide.")]
        [Range(0f, 1f)] public float reactionTime = 0.15f;

        [Tooltip("How fast awareness rises when target is fully visible at close range.")]
        [Range(0.1f, 10f)] public float sightRiseSpeed = 1.5f;

        [Tooltip("How fast awareness decays when target leaves sight.")]
        [Range(0.05f, 3f)] public float sightDecaySpeed = 0.08f; // slow -- player stays suspicious longer

        [Header("Hearing")]
        [Range(1f, 40f)] public float hearingRange = 10f;

        [Tooltip("How fast awareness rises from continuous ambient sound.")]
        [Range(0.1f, 5f)] public float hearingRiseSpeed = 3.0f;

        [Tooltip("Sound intensity (post-propagation) that immediately floors awareness to suspicious.")]
        [Range(0.1f, 1f)] public float soundSuspicionThreshold = 0.4f;

        [Tooltip("Sound intensity (post-propagation) that immediately floors awareness to hostile.")]
        [Range(0.1f, 1f)] public float soundHostileThreshold = 0.8f;

        [Header("Light Detection")]
        [Tooltip("If true, darkness reduces sight detection speed.")]
        public bool useLightDetection = true;

        [Tooltip("Light intensity below which the area counts as dark.")]
        [Range(0f, 2f)] public float darknessThreshold = 0.3f;

        [Tooltip("Awareness rise multiplier in dark areas. 0.2 = 80% reduction.")]
        [Range(0.01f, 1f)] public float darknessFactor = 0.2f;

        [Tooltip("Max distance to search for light sources.")]
        [Range(1f, 30f)] public float lightSearchRadius = 12f;

        [Header("Awareness Dynamics")]
        [Range(0.01f, 3f)] public float decaySpeed = 0.06f; // slow decay -- suspicious state lasts longer
        [Range(0f, 0.15f)] public float noiseAmount = 0.05f;

        // Compatibility properties -- personality and morale system use these
        // They map to sightRiseSpeed and decaySpeed internally
        public float riseSpeed
        {
            get => sightRiseSpeed;
            set => sightRiseSpeed = value;
        }

        [Header("Layer Masks")]
        public LayerMask sightBlockers = Physics.DefaultRaycastLayers;
        public LayerMask targetLayers = Physics.DefaultRaycastLayers;

        // Runtime thresholds set by StealthHuntAI morale system
        [System.NonSerialized] public float suspicionThresh = 0.30f;
        [System.NonSerialized] public float hostileThresh = 0.70f;
        [System.NonSerialized] public float searchDur = 10f;

        // ---------- Runtime properties ----------------------------------------

        public float AwarenessLevel { get; internal set; }

        /// <summary>Set by StealthHuntAI to slow awareness decay in Hostile state.</summary>
        public bool IsHostile { get => _isHostile; set => _isHostile = value; }
        public void AddAwareness(float amount)
        {
            AwarenessLevel = Mathf.Clamp01(AwarenessLevel + amount);
        }

        public bool CanSeeTarget { get; private set; }
        public bool CanHearTarget { get; private set; }
        public float PartialVisibility { get; private set; }
        public float StimulusConfidence { get; private set; }
        public float PositionUncertaintyRadius { get; private set; }
        public Vector3 LastStimulusPosition { get; private set; }

        public StimulusRecord LastStimulus { get; private set; }

        /// <summary>Normalized sight exposure (0-1). Shows detection progress.</summary>
        public float SightAccumulator { get; private set; }

        /// <summary>True when unit has confirmed visual detection this session.</summary>
        public bool SightDetected => CanSeeTarget;

        /// <summary>Debug: sight origin transform.</summary>
        public Transform SightOrigin => _origin;
        /// <summary>Debug: true if target is set.</summary>
        public bool HasTarget => _target != null;
        /// <summary>Debug: true if target is active.</summary>
        public bool TargetIsActive => _target != null && _target.IsActive;

        // ---------- Internal state --------------------------------------------

        private Transform _origin;
        private StealthTarget _target;
        private float _noiseTimer;
        private float _noiseOffset;
        private float _hearingAccumulator;
        private float _reactionTimer;
        private bool _isHostile;

        // Stimulus history
        private StimulusRecord[] _stimulusHistory = new StimulusRecord[8];
        private int _stimulusHead;
        private int _stimulusCount;
        private readonly List<StimulusRecord> _stimulusListCache = new List<StimulusRecord>(8);

        // Light detection cache
        private float _lastLightCheck;
        private float _cachedLightFactor = 1f;
        private const float LightCheckInterval = 0.2f;

        // Shared buffers
        private static readonly RaycastHit[] RaycastBuffer = new RaycastHit[32];
        private const float ConfidenceDecayRate = 0.15f;
        private const float HearingAccumulatorRate = 3.5f;
        private const float MaxUncertaintyRadius = 12f;

        // Set by StealthHuntAI
        [System.NonSerialized] public bool _isPassive = true;
        [System.NonSerialized] public float sightAccumulatorMultiplier = 1f;

        // Legacy fields kept for AutoConfigure sync
        [System.NonSerialized] public float sightAccumulatorRate = 2.0f;
        [System.NonSerialized] public float sightAccumulatorDecay = 0.3f;

        // Persisted
        [SerializeField, HideInInspector] private bool _layerMasksConfigured;

        // ---------- Setup -----------------------------------------------------

        private void Start()
        {
            if (!_layerMasksConfigured)
            {
                int characterMask = LayerMask.GetMask("Character", "Characters",
                    "Player", "Unit", "Units", "Enemy", "Enemies", "NPC");
                int ignoreMask = LayerMask.GetMask("Ignore Raycast", "UI", "TransparentFX");

                sightBlockers = ~(characterMask | ignoreMask);
                targetLayers = Physics.DefaultRaycastLayers;

                if (characterMask != 0)
                    _layerMasksConfigured = true;
            }
        }

        public void Configure(Transform origin, StealthTarget target)
        {
            _origin = origin;
            _target = target;
            HuntDirector.RegisterSightBlockers(sightBlockers);
        }

        public void SetTarget(StealthTarget target)
        {
            _target = target;
        }

        public void MarkLayerMasksConfigured()
        {
            _layerMasksConfigured = true;
        }

        // ---------- Unity lifecycle -------------------------------------------

        private void Update()
        {
            if (_target == null || !_target.IsActive)
            {
                DecayAll();
                return;
            }

            TickNoise();
            EvaluateSenses();
            UpdateAwareness();
            DecayStimulusConfidence();
        }

        // ---------- Sense evaluation ------------------------------------------

        private void EvaluateSenses()
        {
            CanSeeTarget = CheckSight();
            CanHearTarget = CheckHearing();
        }

        private bool CheckSight()
        {
            if (_origin == null) return false;

            Vector3 eyePos = _origin.position;
            // FOV forward: either follows head bone animation or root NavMeshAgent rotation
            Vector3 forward = (sightFollowHeadBone && _origin != null)
                ? _origin.forward
                : transform.forward;

            Vector3 toTarget = _target.PerceptionOrigin - eyePos;
            float distance = toTarget.magnitude;
            float angle = Vector3.Angle(forward, toTarget);

            bool inPeripheral = distance <= peripheralRange;
            bool inCone = distance <= sightRange && angle <= sightAngle * 0.5f;

            if (!inPeripheral && !inCone)
            {
                PartialVisibility = 0f;
                return false;
            }

            // Multi-point visibility check
            int visiblePoints = 0;
            int totalPoints = Mathf.Max(1, sightCheckPoints);

            if (HasClearLine(_origin.position, _target.PerceptionOrigin))
                visiblePoints++;

            if (totalPoints > 1)
            {
                Vector3 right = Vector3.Cross(toTarget.normalized, Vector3.up).normalized;
                int extraPoints = totalPoints - 1;
                float halfSpread = sightCheckSpread * 0.5f;

                for (int i = 0; i < extraPoints; i++)
                {
                    float t = extraPoints == 1 ? 0.5f : (float)i / (extraPoints - 1);
                    float offset = Mathf.Lerp(-halfSpread, halfSpread, t);
                    Vector3 targetPoint = _target.PerceptionOrigin + right * offset;
                    targetPoint += Vector3.up * (i % 2 == 0 ? 0.3f : -0.2f);

                    if (HasClearLine(_origin.position, targetPoint))
                        visiblePoints++;
                }
            }

            PartialVisibility = (float)visiblePoints / totalPoints;

            if (visiblePoints == 0)
                return false;

            // Record sight stimulus
            var record = StimulusRecord.FromSight(_target.PerceptionOrigin);
            RecordStimulus(record);
            LastStimulus = record;
            LastStimulusPosition = _target.PerceptionOrigin;
            StimulusConfidence = 1f;
            PositionUncertaintyRadius = 0f;

            return true;
        }

        private bool CheckHearing()
        {
            if (_target.noiseMultiplier <= 0f)
            {
                _hearingAccumulator = Mathf.Max(0f,
                    _hearingAccumulator - Time.deltaTime * 2f);
                return false;
            }

            float distance = Vector3.Distance(transform.position, _target.Position);
            if (distance > hearingRange)
            {
                _hearingAccumulator = Mathf.Max(0f,
                    _hearingAccumulator - Time.deltaTime * 2f);
                return false;
            }

            // Floor separation penalty
            float floorPenalty = 1f;
            float verticalDist = Mathf.Abs(transform.position.y - _target.Position.y);
            if (verticalDist > 0.8f)
            {
                Vector3 from = new Vector3(_target.Position.x,
                    Mathf.Min(transform.position.y, _target.Position.y) + 0.1f,
                    _target.Position.z);
                if (Physics.Raycast(from, Vector3.up, verticalDist - 0.2f, sightBlockers))
                    floorPenalty = 0.15f;
            }

            // Wall occlusion penalty
            float wallPenalty = 1f;
            Vector3 unitEar = transform.position + Vector3.up * 1.4f;
            Vector3 targetPos = _target.Position + Vector3.up * 0.8f;
            int wallHits = Physics.RaycastNonAlloc(
                unitEar, (targetPos - unitEar).normalized,
                RaycastBuffer, distance, sightBlockers);

            int solidWalls = 0;
            for (int i = 0; i < wallHits; i++)
            {
                Transform t = RaycastBuffer[i].transform;
                if (t == _target.transform || t.IsChildOf(_target.transform)) continue;
                if (t == transform || t.IsChildOf(transform)) continue;
                solidWalls++;
            }
            if (solidWalls >= 2) wallPenalty = 0.05f;
            else if (solidWalls == 1) wallPenalty = 0.25f;

            float distanceFactor = 1f - Mathf.Clamp01(distance / hearingRange);
            float exposureRate = distanceFactor * _target.noiseMultiplier
                                 * HearingAccumulatorRate * floorPenalty * wallPenalty;

            _hearingAccumulator += exposureRate * Time.deltaTime;

            if (_hearingAccumulator >= 1f)
            {
                _hearingAccumulator = 0f;

                // Record hearing stimulus with noisy position
                float noiseRadius = Mathf.Lerp(0.5f, 3f,
                    Mathf.Clamp01(distance / hearingRange));
                Vector3 noise = Random.insideUnitSphere * noiseRadius;
                noise.y = 0f;
                Vector3 noisyPos = _target.PerceptionOrigin + noise;
                float conf = Mathf.Clamp01(distanceFactor);

                LastStimulusPosition = noisyPos;
                StimulusConfidence = conf;
                PositionUncertaintyRadius = noiseRadius;

                var record = StimulusRecord.FromSound(
                    noisyPos,
                    _target.Position - transform.position,
                    conf);
                RecordStimulus(record);
                LastStimulus = record;
                return true;
            }

            return false;
        }

        // ---------- Awareness update ------------------------------------------

        private void UpdateAwareness()
        {
            float sightContribution = 0f;
            float soundContribution = 0f;

            // Sight contribution
            if (CanSeeTarget)
            {
                // Reaction time delay -- awareness doesn't rise immediately
                _reactionTimer += Time.deltaTime;
                if (_reactionTimer < reactionTime) goto skipSight;

                float distanceFactor = 1f - Mathf.Clamp01(
                    Vector3.Distance(transform.position, _target.Position) / sightRange) * 0.5f;

                Vector3 toTarget = _target.PerceptionOrigin - _origin.position;
                Vector3 fwdAware = (sightFollowHeadBone && _origin != null)
                    ? _origin.forward
                    : transform.forward;
                float angle = Vector3.Angle(fwdAware, toTarget);
                bool inCone = angle <= sightAngle * 0.5f;
                float angleFactor = inCone
                    ? 1f - Mathf.Clamp01(angle / (sightAngle * 0.5f)) * 0.3f
                    : 0.5f; // peripheral

                // Light factor
                float lightFactor = GetLightFactor();

                float exposureRate = _target.visibilityMultiplier
                                   * PartialVisibility
                                   * distanceFactor
                                   * angleFactor
                                   * lightFactor;

                sightContribution = exposureRate * sightRiseSpeed
                                  * sightAccumulatorMultiplier
                                  * Time.deltaTime;

                // Update sight accumulator display (0-1)
                SightAccumulator = Mathf.Clamp01(SightAccumulator + exposureRate
                                 * sightAccumulatorMultiplier * Time.deltaTime);
            }
            else
            {
                // Reset reaction timer when target leaves sight
                _reactionTimer = 0f;
                // Decay sight accumulator when not visible
                SightAccumulator = Mathf.Max(0f,
                    SightAccumulator - sightDecaySpeed * Time.deltaTime);
            }
        skipSight:;

            // Hearing contribution (continuous ambient sound)
            if (CanHearTarget)
            {
                float dist = Vector3.Distance(transform.position, _target.Position);
                float factor = (1f - Mathf.Clamp01(dist / hearingRange))
                             * _target.noiseMultiplier;
                soundContribution = factor * hearingRiseSpeed * 0.1f * Time.deltaTime;
            }

            float total = sightContribution + soundContribution;

            if (total > 0f)
            {
                AwarenessLevel = Mathf.Clamp01(
                    AwarenessLevel + total + _noiseOffset * Time.deltaTime);
            }
            else if (AwarenessLevel > 0f)
            {
                // Decay much slower when Hostile -- guards keep searching longer
                float effectiveDecay = _isHostile ? decaySpeed * 0.1f : decaySpeed;
                AwarenessLevel = Mathf.Clamp01(Mathf.MoveTowards(
                    AwarenessLevel, 0f, effectiveDecay * Time.deltaTime));

                SightAccumulator = Mathf.Max(0f,
                    SightAccumulator - sightDecaySpeed * Time.deltaTime);
            }
        }

        // ---------- Light detection -------------------------------------------

        private float GetLightFactor()
        {
            if (!useLightDetection) return 1f;

            if (Time.time - _lastLightCheck < LightCheckInterval)
                return _cachedLightFactor;

            _lastLightCheck = Time.time;

            Vector3 targetPos = _target != null ? _target.Position : transform.position;
            float totalLight = 0f;

            // Use static registry instead of FindObjectsByType -- zero GC, O(n) on active lights
            var lights = LightRegistry.All;
            for (int i = 0; i < lights.Count; i++)
            {
                var light = lights[i];
                if (light == null || !light.enabled) continue;

                if (light.type == LightType.Directional)
                {
                    totalLight += light.intensity * 0.3f;
                    continue;
                }

                float dist = Vector3.Distance(light.transform.position, targetPos);
                if (dist > lightSearchRadius) continue;
                if (dist > light.range) continue;

                float falloff = 1f - Mathf.Clamp01(dist / light.range);
                totalLight += light.intensity * falloff;
            }

            _cachedLightFactor = totalLight < darknessThreshold
                ? darknessFactor
                : Mathf.Clamp01(totalLight);

            return _cachedLightFactor;
        }

        // ---------- Sound stimulus from BroadcastSound -----------------------

        /// <summary>
        /// Called by HuntDirector.BroadcastSound when a sound reaches this unit.
        /// scaledIntensity is already reduced by propagation (distance, walls, corners).
        /// </summary>
        public void HearSound(Vector3 position, float scaledIntensity,
                               Vector3 arrivalDir = default)
        {
            float distance = Vector3.Distance(transform.position, position);
            if (distance > hearingRange) return;
            if (scaledIntensity <= 0f) return;

            // High intensity sounds immediately floor awareness
            if (scaledIntensity >= soundHostileThreshold)
            {
                AwarenessLevel = Mathf.Max(AwarenessLevel, hostileThresh);
            }
            else if (scaledIntensity >= soundSuspicionThreshold)
            {
                AwarenessLevel = Mathf.Max(AwarenessLevel, suspicionThresh);
            }
            else
            {
                // Low intensity -- gradual awareness contribution
                AddAwareness(scaledIntensity * hearingRiseSpeed * Time.deltaTime);
            }

            // Update stimulus position with noise
            float noiseRadius = Mathf.Lerp(0.5f, 4f,
                                    Mathf.Clamp01(distance / hearingRange));
            Vector3 noise = Random.insideUnitSphere * noiseRadius;
            noise.y = 0f;
            Vector3 noisyPos = position + noise;

            if (scaledIntensity > StimulusConfidence * 0.5f)
            {
                LastStimulusPosition = noisyPos;
                StimulusConfidence = Mathf.Clamp01(scaledIntensity);
                PositionUncertaintyRadius = noiseRadius;

                Vector3 dir = arrivalDir.magnitude > 0.1f
                    ? arrivalDir.normalized
                    : (position - transform.position).normalized;

                var record = StimulusRecord.FromSound(noisyPos, dir, scaledIntensity);
                RecordStimulus(record);
                LastStimulus = record;
            }
        }

        // ---------- External awareness modifiers ------------------------------

        public void RaiseAwareness(float amount, Vector3 reportedPosition, float confidence)
        {
            AwarenessLevel = Mathf.Clamp01(AwarenessLevel + amount);

            if (confidence > StimulusConfidence)
            {
                LastStimulusPosition = reportedPosition;
                StimulusConfidence = confidence;
            }
        }

        public void SetIntelPosition(Vector3 noisyPosition, float confidence)
        {
            if (confidence <= StimulusConfidence) return;

            LastStimulusPosition = noisyPosition;
            StimulusConfidence = confidence;
            PositionUncertaintyRadius = Mathf.Lerp(MaxUncertaintyRadius, 3f, confidence);
        }

        // ---------- Decay helpers ---------------------------------------------

        private void DecayAll()
        {
            if (AwarenessLevel > 0f)
                AwarenessLevel = Mathf.Clamp01(Mathf.MoveTowards(
                    AwarenessLevel, 0f, decaySpeed * Time.deltaTime));

            SightAccumulator = Mathf.Max(0f,
                SightAccumulator - sightDecaySpeed * Time.deltaTime);

            CanSeeTarget = false;
            CanHearTarget = false;
        }

        private void DecayStimulusConfidence()
        {
            if (StimulusConfidence > 0f && !CanSeeTarget && !CanHearTarget)
            {
                StimulusConfidence = Mathf.MoveTowards(
                    StimulusConfidence, 0f, ConfidenceDecayRate * Time.deltaTime);
            }

            PositionUncertaintyRadius = (1f - StimulusConfidence) * MaxUncertaintyRadius;
        }

        // ---------- Stimulus history ------------------------------------------

        private void RecordStimulus(StimulusRecord record)
        {
            _stimulusHistory[_stimulusHead] = record;
            _stimulusHead = (_stimulusHead + 1) % _stimulusHistory.Length;
            _stimulusCount = Mathf.Min(_stimulusCount + 1, _stimulusHistory.Length);
        }

        public List<StimulusRecord> GetStimulusHistory()
        {
            _stimulusListCache.Clear();
            for (int i = 0; i < _stimulusCount; i++)
            {
                int idx = (_stimulusHead - 1 - i + _stimulusHistory.Length)
                        % _stimulusHistory.Length;
                _stimulusListCache.Add(_stimulusHistory[idx]);
            }
            return _stimulusListCache;
        }

        // ---------- Noise jitter ----------------------------------------------

        private void TickNoise()
        {
            _noiseTimer += Time.deltaTime;
            if (_noiseTimer >= 0.15f)
            {
                _noiseTimer = 0f;
                _noiseOffset = Random.Range(-noiseAmount, noiseAmount);
            }
        }

        // ---------- Sight helpers ---------------------------------------------

        private bool HasClearLine(Vector3 from, Vector3 to)
        {
            Vector3 dir = to - from;
            float distance = dir.magnitude;

            int hitCount = Physics.RaycastNonAlloc(from, dir.normalized,
                                                    RaycastBuffer, distance,
                                                    sightBlockers);
            for (int i = 0; i < hitCount; i++)
            {
                Transform t = RaycastBuffer[i].transform;
                float dist = RaycastBuffer[i].distance;

                // Skip self hierarchy
                if (t == transform || t.IsChildOf(transform)
                 || transform.IsChildOf(t)) continue;

                // Skip target hierarchy -- the target itself should not block sight
                if (t == _target.transform || t.IsChildOf(_target.transform)
                 || _target.transform.IsChildOf(t)) continue;

                // Skip hits very close to the target endpoint --
                // these are usually the target collider being hit slightly early
                if (dist >= distance - 0.3f) continue;

                return false;
            }
            return true;
        }

        // ---------- Public API ------------------------------------------------

        public void DebugReset()
        {
            AwarenessLevel = 0f;
            StimulusConfidence = 0f;
            PartialVisibility = 0f;
            PositionUncertaintyRadius = 0f;
            SightAccumulator = 0f;
            CanSeeTarget = false;
            CanHearTarget = false;
            _hearingAccumulator = 0f;
            _stimulusHead = 0;
            _stimulusCount = 0;
        }

        // ---------- Gizmos ----------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Transform origin = _origin != null ? _origin : transform;

            // FOV cone
            Gizmos.color = new Color(1f, 1f, 0f, 0.08f);
            float halfAngle = sightAngle * 0.5f * Mathf.Deg2Rad;
            Vector3 gizFwd = (sightFollowHeadBone && _origin != null)
                ? _origin.forward : transform.forward;
            Vector3 left = Quaternion.Euler(0f, -sightAngle * 0.5f, 0f) * gizFwd;
            Vector3 right = Quaternion.Euler(0f, sightAngle * 0.5f, 0f) * gizFwd;
            Gizmos.DrawRay(origin.position, left * sightRange);
            Gizmos.DrawRay(origin.position, right * sightRange);
            Gizmos.DrawRay(origin.position, gizFwd * sightRange);

            // Hearing range
            Gizmos.color = new Color(0f, 0.8f, 1f, 0.04f);
            Gizmos.DrawWireSphere(transform.position, hearingRange);

            // Peripheral range
            Gizmos.color = new Color(1f, 0.5f, 0f, 0.06f);
            Gizmos.DrawWireSphere(transform.position, peripheralRange);

            if (!Application.isPlaying) return;

            // Awareness bar
            Vector3 barBase = transform.position + Vector3.up * 2.5f;
            Gizmos.color = Color.Lerp(Color.green, Color.red, AwarenessLevel);
            Gizmos.DrawLine(barBase, barBase + Vector3.up * AwarenessLevel * 1.5f);

            // Last stimulus
            if (StimulusConfidence > 0.05f)
            {
                Gizmos.color = new Color(1f, 0.3f, 0f, 0.6f);
                Gizmos.DrawWireSphere(LastStimulusPosition, 0.3f);
                if (PositionUncertaintyRadius > 0.1f)
                {
                    Gizmos.color = new Color(1f, 0.3f, 0f, 0.15f);
                    Gizmos.DrawWireSphere(LastStimulusPosition, PositionUncertaintyRadius);
                }
            }

            // Sight origin
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(origin.position + Vector3.up * 0.5f, 0.18f);
        }
#endif
    }
}