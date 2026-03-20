using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// FPS player movement controller.
    /// Handles walk, sprint, crouch, prone, lean and footstep noise.
    /// Requires CharacterController component.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Input")]
        [Tooltip("Input bindings -- create via Assets -> Create -> StealthHuntAI -> Input Config")]
        public InputConfig inputConfig;

        [Header("Movement Speeds")]
        public float walkSpeed = 4.0f;
        public float sprintSpeed = 7.0f;
        public float crouchSpeed = 2.0f;
        public float proneSpeed = 0.8f;
        public float leanSpeed = 8.0f;

        [Header("Heights")]
        public float standHeight = 1.8f;
        public float crouchHeight = 1.1f;
        public float proneHeight = 0.4f;
        public float heightTransitionSpeed = 8f;

        [Header("Lean")]
        public float leanAngle = 20f;
        public float leanOffset = 0.4f;

        [Header("Camera")]
        public Transform cameraRoot;
        public float mouseSensitivity = 0.15f;
        public float maxLookAngle = 80f;

        [Header("Gravity")]
        public float gravity = -18f;
        public float jumpHeight = 1.2f;

        [Header("Noise")]
        [Tooltip("HuntDirector noise radius per movement state.")]
        public float walkNoiseRadius = 4f;
        public float sprintNoiseRadius = 9f;
        public float crouchNoiseRadius = 1.5f;
        public float proneNoiseRadius = 0.3f;
        public float noiseInterval = 0.4f;

        // ---------- State ----------------------------------------------------

        public bool IsSprinting { get; private set; }
        public bool IsCrouching { get; private set; }
        public bool IsProne { get; private set; }
        public bool IsMoving => _moveVel.magnitude > 0.1f;
        public float NoiseLevel => GetNoiseLevel();

        // ---------- Private --------------------------------------------------

        private CharacterController _cc;
        private Vector3 _moveVel;
        private Vector3 _vertVel;
        private float _pitch;
        private float _yaw;
        private float _targetHeight;
        private float _leanTarget;
        private float _leanCurrent;
        private float _noiseTimer;
        private StealthTarget _stealthTarget;

        // Input
        private Vector2 _moveInput;
        private Vector2 _lookInput;
        private bool _sprintHeld;
        private bool _crouchPressed;
        private bool _pronePressed;
        private bool _leanLeft;
        private bool _leanRight;

        // ---------- Unity lifecycle ------------------------------------------

        private void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _stealthTarget = GetComponent<StealthTarget>();
            _targetHeight = standHeight;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            ReadInput();
            HandlePostureToggle();
            UpdateHeight();
            UpdateLean();
            UpdateMovement();
            UpdateLook();
            UpdateNoise();
        }

        // ---------- Input ----------------------------------------------------

        private void ReadInput()
        {
            if (inputConfig != null)
            {
                _moveInput = inputConfig.MoveInput;
                _lookInput = inputConfig.LookInput;
                _sprintHeld = inputConfig.SprintHeld;
                _leanLeft = inputConfig.LeanLeftHeld;
                _leanRight = inputConfig.LeanRightHeld;
                if (inputConfig.CrouchPressed) _crouchPressed = true;
                if (inputConfig.PronePressed) _pronePressed = true;
                if (inputConfig.JumpPressed) TryJump();
            }
            else
            {
                // Fallback defaults
                _moveInput = new Vector2(
                    (Keyboard.current.dKey.isPressed ? 1 : 0) -
                    (Keyboard.current.aKey.isPressed ? 1 : 0),
                    (Keyboard.current.wKey.isPressed ? 1 : 0) -
                    (Keyboard.current.sKey.isPressed ? 1 : 0));
                _lookInput = Mouse.current.delta.ReadValue();
                _sprintHeld = Keyboard.current.leftShiftKey.isPressed;
                _leanLeft = Keyboard.current.qKey.isPressed;
                _leanRight = Keyboard.current.eKey.isPressed;
                if (Keyboard.current.cKey.wasPressedThisFrame) _crouchPressed = true;
                if (Keyboard.current.zKey.wasPressedThisFrame) _pronePressed = true;
            }
        }

        // ---------- Posture --------------------------------------------------

        private void HandlePostureToggle()
        {
            if (_pronePressed)
            {
                _pronePressed = false;
                if (IsProne)
                {
                    IsProne = false;
                    IsCrouching = false;
                    _targetHeight = standHeight;
                }
                else
                {
                    IsProne = true;
                    IsCrouching = false;
                    _targetHeight = proneHeight;
                }
                return;
            }

            if (_crouchPressed)
            {
                _crouchPressed = false;
                if (IsProne)
                {
                    IsProne = false;
                    IsCrouching = true;
                    _targetHeight = crouchHeight;
                }
                else if (IsCrouching)
                {
                    IsCrouching = false;
                    _targetHeight = standHeight;
                }
                else
                {
                    IsCrouching = true;
                    _targetHeight = crouchHeight;
                }
            }
        }

        private void UpdateHeight()
        {
            float current = _cc.height;
            float target = _targetHeight;
            if (Mathf.Abs(current - target) < 0.01f) return;

            float newH = Mathf.Lerp(current, target, heightTransitionSpeed * Time.deltaTime);
            _cc.height = newH;
            _cc.center = Vector3.up * (newH * 0.5f);

            if (cameraRoot != null)
            {
                Vector3 camPos = cameraRoot.localPosition;
                camPos.y = newH - 0.15f;
                cameraRoot.localPosition = camPos;
            }
        }

        // ---------- Lean -----------------------------------------------------

        private void UpdateLean()
        {
            float target = 0f;
            if (_leanLeft && !IsProne) target = -leanAngle;
            if (_leanRight && !IsProne) target = leanAngle;

            _leanCurrent = Mathf.Lerp(_leanCurrent, target, leanSpeed * Time.deltaTime);

            if (cameraRoot != null)
            {
                Vector3 euler = cameraRoot.localEulerAngles;
                euler.z = -_leanCurrent;
                cameraRoot.localEulerAngles = euler;

                Vector3 pos = cameraRoot.localPosition;
                pos.x = (_leanCurrent / leanAngle) * leanOffset;
                cameraRoot.localPosition = pos;
            }
        }

        // ---------- Movement -------------------------------------------------

        private void UpdateMovement()
        {
            IsSprinting = _sprintHeld && !IsCrouching && !IsProne
                        && _moveInput.magnitude > 0.1f;

            float speed = IsProne ? proneSpeed
                        : IsCrouching ? crouchSpeed
                        : IsSprinting ? sprintSpeed
                        : walkSpeed;

            Vector3 move = transform.right * _moveInput.x
                         + transform.forward * _moveInput.y;
            move = Vector3.ClampMagnitude(move, 1f) * speed;

            _moveVel = move;

            if (_cc.isGrounded && _vertVel.y < 0f)
                _vertVel.y = -2f;

            _vertVel.y += gravity * Time.deltaTime;

            _cc.Move((_moveVel + _vertVel) * Time.deltaTime);
        }

        // ---------- Look -----------------------------------------------------

        private void UpdateLook()
        {
            _yaw += _lookInput.x * mouseSensitivity;
            _pitch -= _lookInput.y * mouseSensitivity;
            _pitch = Mathf.Clamp(_pitch, -maxLookAngle, maxLookAngle);

            transform.localRotation = Quaternion.Euler(0f, _yaw, 0f);
            if (cameraRoot != null)
            {
                Vector3 euler = cameraRoot.localEulerAngles;
                euler.x = _pitch;
                cameraRoot.localEulerAngles = euler;
            }
        }

        private void TryJump()
        {
            if (!_cc.isGrounded) return;
            if (IsCrouching || IsProne) return;
            _vertVel.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        // ---------- Noise ----------------------------------------------------

        private void UpdateNoise()
        {
            if (!IsMoving) return;

            _noiseTimer += Time.deltaTime;
            if (_noiseTimer < noiseInterval) return;
            _noiseTimer = 0f;

            float radius = GetNoiseLevel();
            if (radius <= 0f) return;

            HuntDirector.BroadcastSound(transform.position, 0.3f, radius);
        }

        private float GetNoiseLevel()
        {
            if (!IsMoving) return 0f;
            if (IsProne) return proneNoiseRadius;
            if (IsCrouching) return crouchNoiseRadius;
            if (IsSprinting) return sprintNoiseRadius;
            return walkNoiseRadius;
        }
    }
}