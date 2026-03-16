using UnityEngine;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Guard weapon system. Guard shoots at player when Hostile.
    /// Attach to same GameObject as StealthHuntAI.
    /// </summary>
    public class GuardWeapon : MonoBehaviour
    {
        [Header("Shooting")]
        [Range(1f, 10f)] public float fireRate = 1.5f;
        [Range(1, 100)] public int damage = 20;
        [Range(1f, 50f)] public float shootRange = 20f;

        [Tooltip("Bullet spread angle in degrees. Higher = less accurate.")]
        [Range(0f, 15f)] public float spread = 4f;

        [Tooltip("Seconds after losing sight before guard stops shooting.")]
        [Range(0f, 3f)] public float shootMemory = 1.5f;

        [Header("Muzzle Flash")]
        [Tooltip("Optional particle effect for muzzle flash.")]
        public ParticleSystem muzzleFlash;

        // ---------- Internal --------------------------------------------------

        private StealthHuntAI _ai;
        private AwarenessSensor _sensor;
        private float _fireTimer;
        private float _lastSawPlayerTime;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
            _sensor = GetComponent<AwarenessSensor>();
        }

        private void Update()
        {
            if (_ai == null || _ai.CurrentAlertState != AlertState.Hostile) return;

            // Track last time we saw player
            if (_sensor != null && _sensor.CanSeeTarget)
                _lastSawPlayerTime = Time.time;

            // Only shoot within memory window
            bool shouldShoot = Time.time - _lastSawPlayerTime < shootMemory;
            if (!shouldShoot) return;

            _fireTimer += Time.deltaTime;
            if (_fireTimer < fireRate) return;
            _fireTimer = 0f;

            Shoot();
        }

        // ---------- Internal --------------------------------------------------

        private void Shoot()
        {
            // Emit gunshot sound -- alerts nearby units
            SoundStimulus.Emit(transform.position, SoundType.Gunshot);

            if (muzzleFlash != null)
                muzzleFlash.Play();

            // Raycast toward last known player position with spread
            Vector3 origin = transform.position + Vector3.up * 1.4f;
            Vector3 targetPos = _ai.LastKnownPosition.HasValue
                ? _ai.LastKnownPosition.Value + Vector3.up * 0.8f
                : origin + transform.forward * shootRange;

            Vector3 dir = (targetPos - origin).normalized;

            // Apply spread
            dir += Random.insideUnitSphere * (spread * Mathf.Deg2Rad);
            dir = dir.normalized;

            if (Physics.Raycast(origin, dir, out RaycastHit hit, shootRange))
            {
                var playerHealth = hit.collider.GetComponentInParent<PlayerHealth>();
                if (playerHealth != null)
                    playerHealth.TakeDamage(damage);
            }
        }
    }
}