using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthHuntAI.Demo
{
    [RequireComponent(typeof(StealthTarget))]
    public class PlayerStealth : MonoBehaviour
    {
        [Header("Visibility")]
        [Range(0f, 1f)] public float standingVisibility = 1.0f;
        [Range(0f, 1f)] public float crouchVisibility = 0.25f;

        [Header("Noise")]
        [Range(0f, 2f)] public float walkNoise = 1.0f;
        [Range(0f, 2f)] public float crouchNoise = 0.2f;
        [Range(0f, 2f)] public float sprintNoise = 1.8f;

        [Tooltip("Speed above which player counts as sprinting.")]
        [Range(1f, 10f)] public float sprintSpeedThreshold = 5f;

        [Header("Footsteps")]
        [Range(0.1f, 1f)] public float walkStepInterval = 0.45f;
        [Range(0.1f, 1f)] public float sprintStepInterval = 0.28f;
        [Range(0.1f, 1f)] public float crouchStepInterval = 0.7f;

        [Header("Crouch")]
        [Range(0.5f, 1.5f)] public float crouchHeight = 1.0f;
        [Range(1f, 2.5f)] public float standHeight = 2.0f;
        [Range(1f, 20f)] public float crouchSpeed = 10f;

        public bool IsCrouching { get; private set; }
        public bool IsSprinting { get; private set; }
        public bool IsMoving { get; private set; }

        private StealthTarget _stealth;
        private CharacterController _cc;
        private float _footstepTimer;
        private InputAction _crouchAction;

        private void Awake()
        {
            _stealth = GetComponent<StealthTarget>();
            _cc = GetComponent<CharacterController>();

            _crouchAction = new InputAction("Crouch", InputActionType.Button);
            _crouchAction.AddBinding("<Keyboard>/leftCtrl");
            _crouchAction.AddBinding("<Keyboard>/c");
            _crouchAction.Enable();
        }

        private void OnDestroy()
        {
            _crouchAction?.Disable();
            _crouchAction?.Dispose();
        }

        private void Update()
        {
            HandleCrouch();
            HandleVisibilityAndNoise();
            HandleFootsteps();
        }

        private void HandleCrouch()
        {
            IsCrouching = _crouchAction != null && _crouchAction.IsPressed();

            float target = IsCrouching ? crouchHeight : standHeight;
            if (_cc != null)
            {
                _cc.height = Mathf.Lerp(_cc.height, target, crouchSpeed * Time.deltaTime);
                _cc.center = new Vector3(0f, _cc.height * 0.5f, 0f);
            }
        }

        private void HandleVisibilityAndNoise()
        {
            if (_stealth == null) return;

            float speed = _cc != null ? _cc.velocity.magnitude : 0f;
            IsMoving = speed > 0.1f;
            IsSprinting = !IsCrouching && speed > sprintSpeedThreshold;

            if (IsCrouching)
            {
                _stealth.visibilityMultiplier = crouchVisibility;
                _stealth.noiseMultiplier = IsMoving ? crouchNoise : 0f;
            }
            else if (IsSprinting)
            {
                _stealth.visibilityMultiplier = standingVisibility;
                _stealth.noiseMultiplier = sprintNoise;
            }
            else
            {
                _stealth.visibilityMultiplier = standingVisibility;
                _stealth.noiseMultiplier = IsMoving ? walkNoise : 0f;
            }
        }

        private void HandleFootsteps()
        {
            if (_cc == null || !IsMoving || !_cc.isGrounded) return;

            _footstepTimer += Time.deltaTime;

            float interval;
            SoundType type;

            if (IsCrouching) { interval = crouchStepInterval; type = SoundType.Footstep; }
            else if (IsSprinting) { interval = sprintStepInterval; type = SoundType.FootstepHard; }
            else { interval = walkStepInterval; type = SoundType.Footstep; }

            if (_footstepTimer >= interval)
            {
                _footstepTimer = 0f;
                SoundStimulus.Emit(transform.position, type);
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!Application.isPlaying || _stealth == null) return;
            if (_stealth.noiseMultiplier <= 0f) return;

            float radius = 6f * _stealth.noiseMultiplier;
            Gizmos.color = new Color(0f, 1f, 0.5f, 0.1f);
            Gizmos.DrawSphere(transform.position, radius);
            Gizmos.color = new Color(0f, 1f, 0.5f, 0.4f);
            Gizmos.DrawWireSphere(transform.position, radius);
        }
#endif
    }
}