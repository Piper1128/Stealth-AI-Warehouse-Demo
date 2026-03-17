using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI
{
    // ---------- Interface -----------------------------------------------------

    /// <summary>
    /// Movement abstraction for StealthHuntAI.
    /// Implement this on any component to replace the default NavMeshAgent movement.
    ///
    /// Usage:
    ///   1. Create a MonoBehaviour that implements IStealthMovement
    ///   2. Add it to your enemy GameObject
    ///   3. Assign it to the "Movement Provider" field on StealthHuntAI
    ///
    /// If no provider is assigned, StealthHuntAI auto-configures NavMeshMovement.
    /// </summary>
    public interface IStealthMovement
    {
        /// <summary>Move toward a world position.</summary>
        void MoveTo(Vector3 position);

        /// <summary>Stop all movement and clear any active path.</summary>
        void Stop();

        /// <summary>True if a path is currently active.</summary>
        bool HasPath { get; }

        /// <summary>Remaining distance to the current destination.</summary>
        float RemainingDistance { get; }

        /// <summary>True if the agent is on a valid surface and can navigate.</summary>
        bool IsOnSurface { get; }

        /// <summary>Current movement speed in world units per second.</summary>
        float Speed { get; set; }

        /// <summary>Actual movement velocity this frame (0 when standing still).</summary>
        float ActualSpeed { get; }

        /// <summary>
        /// If true, StealthHuntAI and the morale system may override Speed.
        /// Set to false on custom providers that manage their own speed internally.
        /// </summary>
        bool CanOverrideSpeed { get; }
    }

    // ---------- NavMesh default implementation --------------------------------

    /// <summary>
    /// Default IStealthMovement implementation using Unity's NavMeshAgent.
    /// Auto-added by StealthHuntAI if no custom provider is assigned.
    /// Visible in the inspector so NavMeshAgent settings can be tuned here.
    /// </summary>
    [AddComponentMenu("StealthHuntAI/NavMesh Movement")]
    [RequireComponent(typeof(NavMeshAgent))]
    public class NavMeshMovement : MonoBehaviour, IStealthMovement
    {
        // ---------- Inspector -------------------------------------------------

        [Header("Movement")]
        [Tooltip("Base movement speed. Morale system will scale this at runtime.")]
        [Range(0.5f, 15f)] public float baseSpeed = 3.5f;

        [Tooltip("Angular speed in degrees per second.")]
        [Range(60f, 720f)] public float angularSpeed = 240f;

        [Tooltip("How fast the agent accelerates.")]
        [Range(1f, 50f)] public float acceleration = 12f;

        [Tooltip("Distance from destination at which the agent stops.")]
        [Range(0f, 2f)] public float stoppingDistance = 0.1f;

        [Tooltip("If true, StealthHuntAI and the morale system may adjust speed. " +
                 "Set false if your character controller manages speed internally.")]
        public bool canOverrideSpeed = true;

        // ---------- IStealthMovement ------------------------------------------

        public bool HasPath => _agent != null && _agent.hasPath;
        public bool IsOnSurface => _agent != null && _agent.isOnNavMesh;
        public bool CanOverrideSpeed => canOverrideSpeed;

        public float RemainingDistance =>
            _agent != null && _agent.isOnNavMesh && _agent.hasPath
                ? _agent.remainingDistance
                : float.MaxValue;

        public float Speed
        {
            get => _agent != null ? _agent.speed : baseSpeed;
            set { if (_agent != null) _agent.speed = value; }
        }

        public float ActualSpeed
            => _agent != null ? _agent.velocity.magnitude : 0f;

        public void MoveTo(Vector3 position)
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.SetDestination(position);
        }

        public void Stop()
        {
            if (_agent != null && _agent.isOnNavMesh && _agent.hasPath)
                _agent.ResetPath();
        }

        // ---------- Internal --------------------------------------------------

        private NavMeshAgent _agent;

        private void Awake()
        {
            // Ensure NavMeshAgent exists -- AddComponent if missing
            _agent = GetComponent<NavMeshAgent>();
            if (_agent == null)
                _agent = gameObject.AddComponent<NavMeshAgent>();

            ApplySettings();
        }

        private void OnValidate()
        {
            // Live-update agent settings when changed in inspector
            if (_agent == null)
                _agent = GetComponent<NavMeshAgent>();
            ApplySettings();
        }

        private void ApplySettings()
        {
            if (_agent == null) return;
            _agent.speed = baseSpeed;
            _agent.angularSpeed = angularSpeed;
            _agent.acceleration = acceleration;
            _agent.stoppingDistance = stoppingDistance;
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>
        /// Direct access to the underlying NavMeshAgent for advanced use.
        /// </summary>
        public NavMeshAgent Agent => _agent;

        /// <summary>
        /// Warp the agent to a position instantly, bypassing pathfinding.
        /// Useful for respawning or teleporting.
        /// </summary>
        public void WarpTo(Vector3 position)
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.Warp(position);
        }

        // ---------- Gizmos ----------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying) return;
            if (_agent == null || !_agent.hasPath) return;

            // Draw current NavMesh path
            var corners = _agent.path.corners;
            if (corners.Length < 2) return;

            Gizmos.color = new Color(0.4f, 0.9f, 1f, 0.6f);
            for (int i = 0; i < corners.Length - 1; i++)
                Gizmos.DrawLine(corners[i], corners[i + 1]);

            Gizmos.DrawWireSphere(corners[corners.Length - 1], 0.2f);
        }
    }
}