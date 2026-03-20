using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthHuntAI.Demo
{
    [RequireComponent(typeof(PlayerHealth))]
    public class PlayerCombat : MonoBehaviour
    {
        [Header("Weapon")]
        public float damage = 25f;
        public float penetration = 0.5f;
        public float range = 80f;
        public float fireRate = 0.12f;
        public int burstCount = 3;
        public float burstDelay = 0.08f;
        public float reloadTime = 2.0f;
        public int magazineSize = 30;
        public float meleeDamage = 50f;
        public float meleeRange = 1.8f;
        public float meleeCooldown = 0.8f;

        [Header("Recoil")]
        public float recoilUp = 2.5f;
        public float recoilSide = 0.8f;
        public float recoilRecovery = 6f;

        [Header("ADS")]
        public float adsFOV = 40f;
        public float normalFOV = 70f;
        public float adsSpeed = 10f;
        public float adsAccuracyBonus = 0.5f;

        [Header("Spread")]
        public float baseSpread = 0.02f;
        public float sprintSpread = 0.08f;
        public float moveSpread = 0.04f;

        [Header("Suppression")]
        public float suppressionRadius = 3f;
        public float suppressionAmount = 0.4f;

        [Header("Input")]
        [Tooltip("Input bindings -- create via Assets -> Create -> StealthHuntAI -> Input Config")]
        public InputConfig inputConfig;

        [Header("Refs")]
        public Camera playerCamera;
        public LayerMask shootLayers = Physics.DefaultRaycastLayers;
        [Tooltip("Muzzle transform for visual effects. Raycast still fires from camera for accuracy.")]
        public Transform muzzle;

        public enum FireMode { Single, Burst, Auto }
        public FireMode CurrentFireMode { get; private set; } = FireMode.Auto;
        public bool IsADS { get; private set; }
        public bool IsReloading { get; private set; }
        public int CurrentAmmo { get; private set; }
        /// <summary>Reload progress 0-1. Use for reload bar UI.</summary>
        public float ReloadProgress => IsReloading ? _reloadTimer / reloadTime : 0f;

        private PlayerHealth _health;
        private PlayerController _controller;
        private float _fireCooldown;
        private float _reloadTimer;
        private float _meleeCooldown;
        private float _recoilPitch;
        private float _recoilYaw;
        private int _burstFired;
        private float _burstTimer;
        private bool _triggerHeld;

        private void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            _controller = GetComponent<PlayerController>();
            CurrentAmmo = magazineSize;
            if (playerCamera == null) playerCamera = Camera.main;
        }

        private void Update()
        {
            if (_health.IsDead) return;
            ReadInput();
            UpdateADS();
            UpdateRecoilRecovery();
            UpdateReload();
            UpdateMelee();
            UpdateBurst();
            if (_fireCooldown > 0f) _fireCooldown -= Time.deltaTime;
        }

        private void ReadInput()
        {
            if (inputConfig != null)
            {
                _triggerHeld = inputConfig.ShootHeld;
                if (inputConfig.ShootPressed) TryFire();
                IsADS = inputConfig.ADSHeld;
                if (inputConfig.ReloadPressed) TryReload();
                if (inputConfig.MeleePressed) TryMelee();
                if (inputConfig.FireModePressed) CycleFireMode();
            }
            else
            {
                _triggerHeld = Mouse.current.leftButton.isPressed;
                if (Mouse.current.leftButton.wasPressedThisFrame) TryFire();
                IsADS = Mouse.current.rightButton.isPressed;
                if (Keyboard.current.rKey.wasPressedThisFrame) TryReload();
                if (Keyboard.current.fKey.wasPressedThisFrame) TryMelee();
                if (Keyboard.current.vKey.wasPressedThisFrame) CycleFireMode();
            }

            if (CurrentFireMode == FireMode.Auto && _triggerHeld
             && _fireCooldown <= 0f && !IsReloading && CurrentAmmo > 0)
                FireOnce();
        }

        private void TryFire()
        {
            if (IsReloading || _fireCooldown > 0f) return;
            if (CurrentAmmo <= 0) { TryReload(); return; }
            switch (CurrentFireMode)
            {
                case FireMode.Single:
                    FireOnce(); _fireCooldown = fireRate * 2f; break;
                case FireMode.Burst:
                    _burstFired = 0; _burstTimer = 0f; FireBurstStep(); break;
                case FireMode.Auto:
                    FireOnce(); _fireCooldown = fireRate; break;
            }
        }

        private void UpdateBurst()
        {
            if (CurrentFireMode != FireMode.Burst || _burstFired <= 0
             || _burstFired >= burstCount) return;
            _burstTimer += Time.deltaTime;
            if (_burstTimer >= burstDelay) { _burstTimer = 0f; FireBurstStep(); }
        }

        private void FireBurstStep()
        {
            if (CurrentAmmo <= 0 || _burstFired >= burstCount) return;
            FireOnce(); _burstFired++;
        }

        private void FireOnce()
        {
            CurrentAmmo--;
            float spread = baseSpread;
            if (_controller != null)
            {
                if (_controller.IsSprinting) spread = sprintSpread;
                else if (_controller.IsMoving) spread += moveSpread;
            }
            if (IsADS) spread *= (1f - adsAccuracyBonus);

            Vector3 origin = playerCamera.transform.position;
            Vector3 dir = playerCamera.transform.forward
                           + playerCamera.transform.right * Random.Range(-spread, spread)
                           + playerCamera.transform.up * Random.Range(-spread, spread);
            dir.Normalize();

            // Visual effects from muzzle (if assigned)
            if (muzzle != null)
                OnMuzzleFlash(muzzle.position, muzzle.forward);

            if (Physics.Raycast(origin, dir, out RaycastHit hit, range, shootLayers))
            {
                bool headshot = false;
                try { headshot = hit.collider.CompareTag("Head"); } catch { }
                var info = DamageInfo.FromBullet(
                    headshot ? damage * 2.5f : damage,
                    penetration, dir, hit.point, headshot);
                info.sourceTag = "Player";
                // Search up hierarchy -- works regardless of which collider was hit
                var gh = hit.collider.GetComponentInParent<GuardHealth>(true);
                gh?.TakeDamage(info);
                ApplySuppressionSplash(hit.point, dir);
            }
            else
                ApplySuppressionSplash(origin + dir * range, dir);

            _recoilPitch += recoilUp;
            _recoilYaw += Random.Range(-recoilSide, recoilSide);
            _fireCooldown = fireRate;
            if (CurrentAmmo <= 0) TryReload();
        }

        private void ApplySuppressionSplash(Vector3 point, Vector3 dir)
        {
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null) continue;
                float dist = Vector3.Distance(point, units[i].transform.position);
                if (dist > suppressionRadius) continue;
                float amount = suppressionAmount * (1f - dist / suppressionRadius);
                units[i].AddSuppression(amount);
                // Near miss -- guard heard the shot and becomes hostile
                if (dist < suppressionRadius * 0.5f)
                    units[i].ForceHostile();
            }
        }

        private void UpdateADS()
        {
            if (playerCamera == null) return;
            playerCamera.fieldOfView = Mathf.Lerp(
                playerCamera.fieldOfView,
                IsADS ? adsFOV : normalFOV,
                adsSpeed * Time.deltaTime);
        }

        private void UpdateRecoilRecovery()
        {
            _recoilPitch = Mathf.Lerp(_recoilPitch, 0f, recoilRecovery * Time.deltaTime);
            _recoilYaw = Mathf.Lerp(_recoilYaw, 0f, recoilRecovery * Time.deltaTime);
            if (playerCamera != null)
                playerCamera.transform.localRotation *= Quaternion.Euler(
                    -_recoilPitch * Time.deltaTime, _recoilYaw * Time.deltaTime, 0f);
        }

        private void TryReload()
        {
            if (IsReloading || CurrentAmmo >= magazineSize) return;
            IsReloading = true; _reloadTimer = 0f;
        }

        private void UpdateReload()
        {
            if (!IsReloading) return;
            _reloadTimer += Time.deltaTime;
            if (_reloadTimer >= reloadTime)
            {
                IsReloading = false;
                CurrentAmmo = magazineSize;
            }
        }

        private void TryMelee()
        {
            if (_meleeCooldown > 0f) return;
            _meleeCooldown = meleeCooldown;
            if (!Physics.Raycast(playerCamera.transform.position,
                playerCamera.transform.forward, out RaycastHit hit,
                meleeRange, shootLayers)) return;
            var info = new DamageInfo
            {
                damage = meleeDamage,
                penetration = 1f,
                direction = playerCamera.transform.forward,
                hitPoint = hit.point,
                sourceTag = "Player"
            };
            hit.collider.GetComponentInParent<GuardHealth>()?.TakeDamage(info);
        }

        private void UpdateMelee()
        {
            if (_meleeCooldown > 0f) _meleeCooldown -= Time.deltaTime;
        }

        private void CycleFireMode()
        {
            CurrentFireMode = CurrentFireMode switch
            {
                FireMode.Single => FireMode.Burst,
                FireMode.Burst => FireMode.Auto,
                _ => FireMode.Single
            };
        }

        /// <summary>
        /// Override to add muzzle flash, particle effects, sound etc.
        /// Called every shot with muzzle world position and forward direction.
        /// </summary>
        protected virtual void OnMuzzleFlash(Vector3 position, Vector3 forward) { }
    }
}