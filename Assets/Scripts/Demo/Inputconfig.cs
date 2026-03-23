using UnityEngine;
using UnityEngine.InputSystem;

namespace StealthHuntAI.Demo
{
    /// <summary>
    /// ScriptableObject defining all player input bindings.
    /// Create via Assets -> Create -> StealthHuntAI -> Input Config
    /// Assign to PlayerController and PlayerCombat inspector fields.
    /// </summary>
    public enum MouseButton
    {
        LeftButton = 0,
        RightButton = 1,
        MiddleButton = 2,
    }

    [CreateAssetMenu(
        fileName = "InputConfig",
        menuName = "StealthHuntAI/Input Config",
        order = 2)]
    public class InputConfig : ScriptableObject
    {
        [Header("Movement")]
        public Key forward = Key.W;
        public Key back = Key.S;
        public Key left = Key.A;
        public Key right = Key.D;
        public Key sprint = Key.LeftShift;
        public Key crouch = Key.C;
        public Key prone = Key.Z;
        public Key leanLeft = Key.Q;
        public Key leanRight = Key.E;
        public Key jump = Key.Space;

        [Header("Combat")]
        public Key reload = Key.R;
        public Key melee = Key.F;
        public Key fireMode = Key.V;

        [Header("Mouse")]
        public MouseButton shootButton = MouseButton.LeftButton;
        public MouseButton adsButton = MouseButton.RightButton;

        // ---------- Helpers --------------------------------------------------

        public Vector2 MoveInput =>
            new Vector2(
                (Keyboard.current[right].isPressed ? 1f : 0f) -
                (Keyboard.current[left].isPressed ? 1f : 0f),
                (Keyboard.current[forward].isPressed ? 1f : 0f) -
                (Keyboard.current[back].isPressed ? 1f : 0f));

        public bool SprintHeld => Keyboard.current[sprint].isPressed;
        public bool CrouchPressed => Keyboard.current[crouch].wasPressedThisFrame;
        public bool PronePressed => Keyboard.current[prone].wasPressedThisFrame;
        public bool LeanLeftHeld => Keyboard.current[leanLeft].isPressed;
        public bool LeanRightHeld => Keyboard.current[leanRight].isPressed;
        public bool JumpPressed => Keyboard.current[jump].wasPressedThisFrame;

        public bool ShootPressed => shootButton == MouseButton.LeftButton
            ? Mouse.current.leftButton.wasPressedThisFrame
            : Mouse.current.rightButton.wasPressedThisFrame;

        public bool ShootHeld => shootButton == MouseButton.LeftButton
            ? Mouse.current.leftButton.isPressed
            : Mouse.current.rightButton.isPressed;

        public bool ADSHeld => adsButton == MouseButton.RightButton
            ? Mouse.current.rightButton.isPressed
            : Mouse.current.leftButton.isPressed;
        public bool ReloadPressed => Keyboard.current[reload].wasPressedThisFrame;
        public bool MeleePressed => Keyboard.current[melee].wasPressedThisFrame;
        public bool FireModePressed => Keyboard.current[fireMode].wasPressedThisFrame;

        public Vector2 LookInput => Mouse.current.delta.ReadValue();

        // ---------- Rebinding ------------------------------------------------

        [System.NonSerialized]
        public bool IsRebinding = false;
        [System.NonSerialized]
        public string RebindingTarget = "";

        /// <summary>
        /// Start listening for next key press to rebind a specific action.
        /// Call with the field name e.g. StartRebind("forward")
        /// </summary>
        public void StartRebind(string targetField)
        {
            IsRebinding = true;
            RebindingTarget = targetField;
        }

        /// <summary>
        /// Call from Update -- checks if a key is pressed and applies rebind.
        /// Returns true when rebind is complete.
        /// </summary>
        public bool PollRebind()
        {
            if (!IsRebinding) return false;

            var kb = Keyboard.current;
            if (kb == null) return false;

            foreach (Key key in System.Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None || key == Key.Escape) continue;
                if (!kb[key].wasPressedThisFrame) continue;

                ApplyRebind(RebindingTarget, key);
                IsRebinding = false;
                RebindingTarget = "";
                return true;
            }
            return false;
        }

        /// <summary>Cancel current rebind without applying.</summary>
        public void CancelRebind()
        {
            IsRebinding = false;
            RebindingTarget = "";
        }

        private void ApplyRebind(string field, Key key)
        {
            switch (field)
            {
                case "forward": forward = key; break;
                case "back": back = key; break;
                case "left": left = key; break;
                case "right": right = key; break;
                case "sprint": sprint = key; break;
                case "crouch": crouch = key; break;
                case "prone": prone = key; break;
                case "leanLeft": leanLeft = key; break;
                case "leanRight": leanRight = key; break;
                case "jump": jump = key; break;
                case "reload": reload = key; break;
                case "melee": melee = key; break;
                case "fireMode": fireMode = key; break;
            }
        }
    }
}