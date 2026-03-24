using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Per-guard model of where the player is estimated to be.
    /// Updated every frame by TacticalBrain based on what this guard can see.
    ///
    /// Guards do not share a single "last known position" -- each has their own
    /// model that gets updated via squad intel sharing.
    /// </summary>
    public class ThreatModel
    {
        // ---------- Known data -----------------------------------------------

        /// <summary>Last position guard directly confirmed player was at.</summary>
        public Vector3 LastKnownPosition { get; private set; }

        /// <summary>Velocity player had when last seen.</summary>
        public Vector3 LastKnownVelocity { get; private set; }

        /// <summary>When guard last directly saw player (Time.time).</summary>
        public float LastSeenTime { get; private set; } = -999f;

        /// <summary>Seconds since guard last saw player.</summary>
        public float TimeSinceSeen => LastSeenTime < 0f ? 0f : Time.time - LastSeenTime;

        /// <summary>True when guard currently has line of sight to player.</summary>
        public bool HasLOS { get; private set; }

        // ---------- Estimated data -------------------------------------------

        /// <summary>
        /// Estimated current player position.
        /// When HasLOS: equals player position.
        /// When no LOS: extrapolated from last known + velocity * time.
        /// </summary>
        public Vector3 EstimatedPosition { get; private set; }

        /// <summary>
        /// Confidence in estimated position (0-1).
        /// 1.0 = guard can see player right now.
        /// Falls to 0 over ConfidenceDecayTime seconds after losing sight.
        /// </summary>
        public float Confidence { get; private set; }

        /// <summary>True when guard has any useful intel (confidence > 0).</summary>
        public bool HasIntel => Confidence > 0.05f;

        // ---------- Settings -------------------------------------------------

        /// <summary>Seconds for confidence to decay from 1 to 0 after losing sight.</summary>
        public float ConfidenceDecayTime = 8f;

        /// <summary>How far extrapolation is trusted. Beyond this, confidence drops faster.</summary>
        public float MaxExtrapolationDist = 20f;

        // ---------- Update ---------------------------------------------------

        /// <summary>Call every frame with current player data.</summary>
        public void UpdateWithSight(Vector3 playerPos, Vector3 playerVelocity)
        {
            LastKnownPosition = playerPos;
            LastKnownVelocity = playerVelocity;
            LastSeenTime = Time.time;
            EstimatedPosition = playerPos;
            Confidence = 1f;
            HasLOS = true;
        }

        /// <summary>Call every frame when guard cannot see player.</summary>
        public void UpdateWithoutSight()
        {
            HasLOS = false;

            if (LastSeenTime < 0f) return; // never seen player

            float elapsed = TimeSinceSeen;

            // Extrapolate position based on last known velocity
            Vector3 extrapolated = LastKnownPosition + LastKnownVelocity * elapsed;

            // Clamp extrapolation distance
            float extrapDist = Vector3.Distance(LastKnownPosition, extrapolated);
            if (extrapDist > MaxExtrapolationDist)
            {
                extrapolated = LastKnownPosition +
                    (extrapolated - LastKnownPosition).normalized * MaxExtrapolationDist;
            }

            // Sample onto NavMesh -- prevents estimated position inside walls
            if (UnityEngine.AI.NavMesh.SamplePosition(extrapolated, out var hit,
                3f, UnityEngine.AI.NavMesh.AllAreas))
                EstimatedPosition = hit.position;
            else
                EstimatedPosition = LastKnownPosition; // fallback to last known

            // Confidence decays over time
            Confidence = Mathf.Clamp01(1f - elapsed / ConfidenceDecayTime);
        }

        /// <summary>
        /// Receive intel from another guard or squad blackboard.
        /// Only updates if intel is more confident than current model.
        /// </summary>
        public void ReceiveIntel(Vector3 position, Vector3 velocity, float confidence)
        {
            // Cap shared intel confidence -- guards know general area but not exact position
            // Prevents wallhack feel where guards track you through walls perfectly
            confidence = Mathf.Min(confidence, 0.7f);

            if (confidence <= Confidence * 0.5f) return;

            // Add position noise proportional to distance -- further intel is less precise
            Vector3 noise = UnityEngine.Random.insideUnitSphere * 2f;
            noise.y = 0f;
            position += noise;

            float blend = confidence / (confidence + Confidence);
            EstimatedPosition = Vector3.Lerp(EstimatedPosition, position, blend);
            LastKnownVelocity = Vector3.Lerp(LastKnownVelocity, velocity, blend);
            Confidence = Mathf.Max(Confidence, confidence * 0.9f);

            // Only update LastKnownPosition from direct sight (handled in UpdateWithSight)
            if (confidence > 0.8f)
            {
                LastKnownPosition = position;
                LastSeenTime = Time.time - (1f - confidence) * ConfidenceDecayTime;
            }
        }

        /// <summary>Reset all intel -- guard has no idea where player is.</summary>
        public void Reset()
        {
            LastSeenTime = -999f;
            Confidence = 0f;
            HasLOS = false;
            EstimatedPosition = Vector3.zero;
            LastKnownVelocity = Vector3.zero;
        }

        /// <summary>Call when entering combat -- prevents stale TimeSinceSeen on first frame.</summary>
        public void OnEnterCombat()
        {
            if (LastSeenTime < 0f)
                LastSeenTime = Time.time;
        }

        // ---------- Search cone ----------------------------------------------

        /// <summary>
        /// Returns true if a world position is within the search cone.
        /// Cone starts narrow in the direction player was moving and widens over time.
        /// </summary>
        public bool IsInSearchCone(Vector3 point, float searchAge)
        {
            if (!HasIntel) return true; // no intel -- search everywhere

            // Cone starts at 30 degrees and expands 8 degrees per second
            float coneAngle = Mathf.Min(180f, 30f + searchAge * 8f);

            Vector3 searchDir = LastKnownVelocity.magnitude > 0.1f
                ? LastKnownVelocity.normalized
                : (point - LastKnownPosition).normalized;

            Vector3 toPoint = (point - EstimatedPosition);
            toPoint.y = 0f;

            if (toPoint.magnitude < 0.1f) return true;

            float angle = Vector3.Angle(searchDir, toPoint.normalized);
            return angle <= coneAngle;
        }
    }
}