using UnityEngine;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Guard weapon with penetration, burst fire, suppression and reload delay.
    /// Implements IWeaponProvider for Core integration.
    /// </summary>
    [RequireComponent(typeof(StealthHuntAI))]
    public class GuardWeapon : MonoBehaviour, IWeaponProvider, IShootable
    {
        [Header("Weapon")]
        public float damage = 15f;
        public float penetration = 0.4f;  // 0=none, 1=full
        public float shootRange = 20f;
        public float accuracy = 0.97f; // 0-1
        public float fireRate = 0.3f;  // seconds between shots
        public int burstCount = 1;     // 1 = single, 3 = burst
        public float burstDelay = 0.1f;
        public float reloadTime = 2.5f;
        public int magazineSize = 20;

        [Header("Suppression")]
        [Tooltip("Radius around bullet path that suppresses player.")]
        public float suppressionRadius = 2.5f;
        public float suppressionAmount = 0.15f;

        [Header("Refs")]
        public Transform muzzle;
        public LayerMask shootLayers = ~0; // all layers by default

        // IWeaponProvider
        public float ShootRange => shootRange;
        public bool IsReady => !_reloading && _fireCooldown <= 0f;

        private StealthHuntAI _ai;
        private AwarenessSensor _sensor;
        private float _fireCooldown;
        private float _reloadTimer;
        private bool _reloading;
        private int _currentAmmo;
        private int _burstFired;
        private float _burstTimer;
        private Vector3 _lastTargetPos;

        private void Awake()
        {
            _ai = GetComponent<StealthHuntAI>();
            _sensor = GetComponent<AwarenessSensor>();
            _currentAmmo = magazineSize;
        }

        private void Start()
        {
            // Auto-register sensor -- needed if component added at runtime
            if (_sensor == null) _sensor = GetComponent<AwarenessSensor>();
        }

        private void Update()
        {
            if (_fireCooldown > 0f) _fireCooldown -= Time.deltaTime;

            if (_reloading)
            {
                _reloadTimer += Time.deltaTime;
                if (_reloadTimer >= reloadTime)
                {
                    _reloading = false;
                    _currentAmmo = magazineSize;
                }
                return;
            }

            // Burst continuation
            if (_burstFired > 0 && _burstFired < burstCount)
            {
                _burstTimer += Time.deltaTime;
                if (_burstTimer >= burstDelay)
                {
                    _burstTimer = 0f;
                    FireOnce();
                    _burstFired++;
                    if (_burstFired >= burstCount) _burstFired = 0;
                }
            }
        }

        /// <summary>Called by StealthHuntAI shooting state to attempt firing.</summary>
        public void TryShoot(Vector3 targetPos)
        {
            if (_reloading || _fireCooldown > 0f) return;
            if (_currentAmmo <= 0) { StartReload(); return; }

            _lastTargetPos = targetPos;
            _burstFired = 1;
            _burstTimer = 0f;
            FireOnce();
            _fireCooldown = fireRate * burstCount;

            if (_currentAmmo <= 0) StartReload();
        }

        private void FireOnce()
        {
            if (_currentAmmo <= 0) return;
            _currentAmmo--;

            // Suppression penalty on accuracy
            float guardHealth = GetComponent<GuardHealth>()?.SuppressLevel ?? 0f;
            float effectiveAccuracy = accuracy * (1f - guardHealth * 0.4f);

            Vector3 origin = muzzle != null ? muzzle.position : transform.position + Vector3.up * 1.5f;
            // Use stored target pos from TryShoot -- supports estimated positions from Combat Pack
            Vector3 targetPos = _lastTargetPos != Vector3.zero
                ? _lastTargetPos + Vector3.up * 0.8f
                : (_ai.GetTarget() != null
                    ? _ai.GetTarget().Position + Vector3.up * 0.8f
                    : transform.position + transform.forward * 10f);
            Vector3 toTarget = targetPos - origin;

            // Line of sight check -- don't shoot through walls
            if (Physics.Raycast(origin, toTarget.normalized, toTarget.magnitude, shootLayers))
            {
                // Something is blocking -- check if it's the player
                if (!Physics.Raycast(origin, toTarget.normalized,
                    out RaycastHit losHit, toTarget.magnitude, shootLayers)
                    || losHit.collider.GetComponentInParent<PlayerHealth>() == null)
                    return; // blocked by wall
            }

            // Apply spread
            float spread = 1f - effectiveAccuracy;
            Vector3 dir = toTarget.normalized
                         + Random.insideUnitSphere * spread;
            dir.Normalize();

            if (Physics.Raycast(origin, dir, out RaycastHit hit,
                shootRange * 1.5f, shootLayers))
            {
                var playerHealth = hit.collider.GetComponentInParent<PlayerHealth>();
                if (playerHealth != null)
                {
                    bool headshot = false;
                    try { headshot = hit.collider.CompareTag("Head"); } catch { }
                    playerHealth.TakeDamage(new DamageInfo
                    {
                        damage = headshot ? damage * 2f : damage,
                        penetration = penetration,
                        direction = dir,
                        hitPoint = hit.point,
                        isHeadshot = headshot,
                        sourceTag = "Guard"
                    });
                }

                // Suppression splash near player
                ApplyPlayerSuppression(hit.point, dir);
            }
        }

        private void ApplyPlayerSuppression(Vector3 point, Vector3 dir)
        {
            var target = _ai.GetTarget();
            if (target == null) return;
            float dist = Vector3.Distance(point, target.Position);
            if (dist > suppressionRadius) return;

            // Send sound stimulus as near-miss indicator
            HuntDirector.BroadcastSound(point, 0.2f, 2f);
        }

        private void StartReload()
        {
            _reloading = true;
            _reloadTimer = 0f;
        }
    }
}