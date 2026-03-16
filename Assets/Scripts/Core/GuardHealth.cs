using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Guard health and death system.
    /// Attach to the same GameObject as StealthHuntAI.
    /// </summary>
    public class GuardHealth : MonoBehaviour
    {
        [Header("Health")]
        [Range(1, 500)] public int maxHealth = 100;

        [Header("Silent Takedown")]
        [Tooltip("Max distance for silent takedown.")]
        [Range(0.5f, 3f)] public float takedownRange = 1.8f;

        [Tooltip("Guard must be facing away from player within this angle.")]
        [Range(90f, 180f)] public float takedownAngle = 130f;

        // ---------- Runtime ---------------------------------------------------

        public int CurrentHealth { get; private set; }
        public bool IsDead { get; private set; }

        // ---------- Internal --------------------------------------------------

        private StealthHuntAI _ai;
        private AwarenessSensor _sensor;
        private Animator _animator;
        private NavMeshAgent _agent;

        private static readonly int HashIsDead = Animator.StringToHash("isDead");

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
            _sensor = GetComponent<AwarenessSensor>();
            _animator = GetComponentInChildren<Animator>();
            _agent = GetComponent<NavMeshAgent>();

            CurrentHealth = maxHealth;
        }

        // ---------- Public API ------------------------------------------------

        /// <summary>Deal damage from gunfire. Triggers sound stimulus.</summary>
        public void TakeDamage(int amount)
        {
            if (IsDead) return;

            CurrentHealth -= amount;

            // Gunshot makes guard instantly alert
            if (_ai != null)
                _ai.ForceAlert(transform.position + transform.forward * 2f, 1f);

            // Emit gunshot sound -- alerts nearby units
            SoundStimulus.Emit(transform.position, SoundType.Gunshot);

            if (CurrentHealth <= 0)
                Die(silent: false);
        }

        /// <summary>Silent takedown -- no sound, no alert.</summary>
        public void SilentKill()
        {
            if (IsDead) return;
            CurrentHealth = 0;
            Die(silent: true);
        }

        /// <summary>
        /// Check if player can perform a silent takedown on this guard.
        /// </summary>
        public bool CanTakedown(Transform player)
        {
            if (IsDead) return false;
            if (_ai != null && _ai.CurrentAlertState == AlertState.Hostile) return false;

            float dist = Vector3.Distance(player.position, transform.position);
            if (dist > takedownRange) return false;

            // Guard must have back toward player
            Vector3 toPlayer = (player.position - transform.position).normalized;
            float dot = Vector3.Dot(transform.forward, toPlayer);
            float angle = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;

            return angle >= takedownAngle * 0.5f;
        }

        // ---------- Internal --------------------------------------------------

        private void Die(bool silent)
        {
            if (IsDead) return;
            IsDead = true;

            // Disable AI
            if (_ai != null) _ai.enabled = false;
            if (_sensor != null) _sensor.enabled = false;

            // Stop movement
            if (_agent != null)
            {
                _agent.ResetPath();
                _agent.enabled = false;
            }

            // Trigger death animation
            if (_animator != null)
                _animator.SetBool(HashIsDead, true);

            // Destroy after animation plays
            Destroy(gameObject, 4f);
        }
    }
}