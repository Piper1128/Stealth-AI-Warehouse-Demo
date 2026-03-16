using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// Player combat -- shooting and silent takedown.
    /// Attach to PlayerCapsule.
    ///
    /// Controls (Input System):
    ///   Left Mouse  -- shoot
    ///   F           -- silent takedown (when behind guard)
    /// </summary>
    public class PlayerCombat : MonoBehaviour
    {
        [Header("Shooting")]
        [Range(1, 100)] public int shootDamage = 34;
        [Range(1f, 100f)] public float shootRange = 40f;

        [Header("Takedown")]
        [Tooltip("Key for silent takedown.")]
        public Key takedownKey = Key.F;

        [Header("References")]
        [Tooltip("Camera used for shooting raycast. Auto-found if empty.")]
        public Camera playerCamera;

        // ---------- Internal --------------------------------------------------

        private InputAction _shootAction;
        private InputAction _takedownAction;
        private GameManager _gameManager;

        // ---------- Unity lifecycle -------------------------------------------

        private void Awake()
        {
            if (playerCamera == null)
                playerCamera = Camera.main;

            _gameManager = FindFirstObjectByType<GameManager>();

            _shootAction = new InputAction("Shoot", InputActionType.Button);
            _shootAction.AddBinding("<Mouse>/leftButton");
            _shootAction.Enable();

            _takedownAction = new InputAction("Takedown", InputActionType.Button);
            _takedownAction.AddBinding("<Keyboard>/f");
            _takedownAction.Enable();
        }

        private void OnDestroy()
        {
            _shootAction?.Disable();
            _shootAction?.Dispose();
            _takedownAction?.Disable();
            _takedownAction?.Dispose();
        }

        private void Update()
        {
            if (_gameManager != null && _gameManager.GameOver) return;

            if (_shootAction.WasPerformedThisFrame())
                TryShoot();

            if (_takedownAction.WasPerformedThisFrame())
                TryTakedown();
        }

        // ---------- Shooting -------------------------------------------------

        private void TryShoot()
        {
            if (playerCamera == null) return;

            Ray ray = playerCamera.ScreenPointToRay(
                new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f));

            // Emit gunshot -- this is loud and will alert nearby guards
            SoundStimulus.Emit(transform.position, SoundType.Gunshot);

            if (!Physics.Raycast(ray, out RaycastHit hit, shootRange)) return;

            var guard = hit.collider.GetComponentInParent<GuardHealth>();
            if (guard != null)
                guard.TakeDamage(shootDamage);
        }

        // ---------- Takedown -------------------------------------------------

        private void TryTakedown()
        {
            // Find all guards within takedown range
            var guards = FindObjectsByType<GuardHealth>(FindObjectsSortMode.None);

            foreach (var guard in guards)
            {
                if (guard.CanTakedown(transform))
                {
                    guard.SilentKill();
                    return; // only takedown one at a time
                }
            }
        }

        // ---------- Gizmos ---------------------------------------------------

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (playerCamera == null) return;

            Gizmos.color = Color.red;
            Gizmos.DrawRay(playerCamera.transform.position,
                           playerCamera.transform.forward * shootRange);
        }
#endif
    }
}