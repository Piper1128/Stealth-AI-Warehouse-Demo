using UnityEngine;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Guard weapon system. Guard shoots at player when Hostile and in range.
    /// Supports burst fire and single shot modes.
    /// Attach to same GameObject as StealthHuntAI.
    /// </summary>
    public class GuardWeapon : MonoBehaviour
    {
        [Header("Shooting")]
        [Range(1f, 20f)] public float shootRange = 20f;
        [Range(1, 100)] public int damage = 20;

        [Tooltip("Bullet spread angle in degrees.")]
        [Range(0f, 15f)] public float spread = 4f;

        [Tooltip("Seconds after losing sight before guard stops shooting.")]
        [Range(0f, 3f)] public float shootMemory = 1.5f;

        [Header("Fire Mode")]
        public FireMode fireMode = FireMode.Burst;

        [Tooltip("Seconds between single shots or between bursts.")]
        [Range(0.1f, 5f)] public float fireInterval = 1.5f;

        [Header("Burst Settings")]
        [Tooltip("Number of shots per burst.")]
        [Range(1, 8)] public int burstCount = 3;

        [Tooltip("Seconds between shots within a burst.")]
        [Range(0.05f, 0.5f)] public float burstRate = 0.12f;

        [Header("Muzzle Flash")]
        public ParticleSystem muzzleFlash;

        // ---------- Enums -------------------------------------------------------

        public enum FireMode { Single, Burst, Auto }

        // ---------- Internal ----------------------------------------------------

        private StealthHuntAI _ai;
        private AwarenessSensor _sensor;
        private float _fireTimer;
        private float _lastSawPlayerTime;
        private int _burstShotsLeft;
        private float _burstTimer;
        private bool _isBursting;

        // ---------- Unity lifecycle ---------------------------------------------

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
        }

        private void Start()
        {
            _sensor = GetComponent<AwarenessSensor>();
        }

        private void Update()
        {
            if (_ai == null || _ai.CurrentAlertState != AlertState.Hostile) return;

            if (_sensor == null)
                _sensor = GetComponent<AwarenessSensor>();

            if (_sensor != null && _sensor.CanSeeTarget)
                _lastSawPlayerTime = Time.time;

            bool shouldShoot = Time.time - _lastSawPlayerTime < shootMemory;
            if (!shouldShoot)
            {
                _isBursting = false;
                _burstShotsLeft = 0;
                return;
            }

            switch (fireMode)
            {
                case FireMode.Single: TickSingle(); break;
                case FireMode.Burst: TickBurst(); break;
                case FireMode.Auto: TickAuto(); break;
            }
        }

        // ---------- Fire modes --------------------------------------------------

        private void TickSingle()
        {
            _fireTimer += Time.deltaTime;
            if (_fireTimer < fireInterval) return;
            _fireTimer = 0f;
            Shoot();
        }

        private void TickBurst()
        {
            if (_isBursting)
            {
                _burstTimer += Time.deltaTime;
                if (_burstTimer >= burstRate)
                {
                    _burstTimer = 0f;
                    Shoot();
                    _burstShotsLeft--;
                    if (_burstShotsLeft <= 0)
                    {
                        _isBursting = false;
                        _fireTimer = 0f;
                    }
                }
            }
            else
            {
                _fireTimer += Time.deltaTime;
                if (_fireTimer >= fireInterval)
                {
                    _isBursting = true;
                    _burstShotsLeft = burstCount;
                    _burstTimer = burstRate; // fire first shot immediately
                }
            }
        }

        private void TickAuto()
        {
            _fireTimer += Time.deltaTime;
            if (_fireTimer < burstRate) return;
            _fireTimer = 0f;
            Shoot();
        }

        // ---------- Shoot -------------------------------------------------------

        private void Shoot()
        {
            SoundStimulus.Emit(transform.position, SoundType.Gunshot);

            if (muzzleFlash != null)
                muzzleFlash.Play();

            Vector3 origin = transform.position + Vector3.up * 1.4f;
            Vector3 targetPos = _ai.LastKnownPosition.HasValue
                ? _ai.LastKnownPosition.Value + Vector3.up * 0.8f
                : origin + transform.forward * shootRange;

            Vector3 dir = (targetPos - origin).normalized;
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