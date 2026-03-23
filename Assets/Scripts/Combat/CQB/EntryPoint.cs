using UnityEngine;

namespace StealthHuntAI.Combat.CQB
{
    /// <summary>
    /// Place this on every door or threshold in your scene.
    /// Defines stack positions, points of domination and the fatal funnel.
    /// Guards find the nearest EntryPoint to the player automatically.
    ///
    /// Setup:
    ///   1. Add EntryPoint to a door GameObject
    ///   2. Set StackLeft and StackRight to positions beside the door
    ///   3. Set DomPointA and DomPointB to far corners inside the room
    ///   4. Set LeadsToRoomID to match the room on the other side
    /// </summary>
    public class EntryPoint : MonoBehaviour
    {
        [Header("Stack positions (outside door)")]
        [Tooltip("Position to the left of the door, hugging the wall.")]
        public Transform stackLeft;

        [Tooltip("Position to the right of the door, hugging the wall.")]
        public Transform stackRight;

        [Header("Points of domination (inside room)")]
        [Tooltip("Far left corner inside the room.")]
        public Transform domPointA;

        [Tooltip("Far right corner inside the room.")]
        public Transform domPointB;

        [Header("Room data")]
        [Tooltip("ID of the room this entry leads into. Match with room tags.")]
        public string leadsToRoomID = "";

        [Tooltip("Width of the fatal funnel zone in front of the door.")]
        [Range(0.5f, 3f)]
        public float fatalFunnelWidth = 1.5f;

        [Tooltip("Can guards use this entry point for CQB?")]
        public bool isBreachable = true;

        // ---------- Runtime state --------------------------------------------

        /// <summary>True when a guard is currently assigned to this entry.</summary>
        public bool IsOccupied { get; private set; }

        private StealthHuntAI _occupant;

        public void Occupy(StealthHuntAI unit)
        {
            IsOccupied = true;
            _occupant = unit;
        }

        public void Release(StealthHuntAI unit)
        {
            if (_occupant == unit)
            {
                IsOccupied = false;
                _occupant = null;
            }
        }

        // ---------- Queries --------------------------------------------------

        public Vector3 StackLeftPos => stackLeft != null
            ? stackLeft.position : transform.position + transform.right * -0.8f;

        public Vector3 StackRightPos => stackRight != null
            ? stackRight.position : transform.position + transform.right * 0.8f;

        public Vector3 DomPosA => domPointA != null
            ? domPointA.position : transform.position + transform.forward * 2f + transform.right * -1.5f;

        public Vector3 DomPosB => domPointB != null
            ? domPointB.position : transform.position + transform.forward * 2f + transform.right * 1.5f;

        /// <summary>Is this position inside the fatal funnel?</summary>
        public bool IsInFatalFunnel(Vector3 pos)
        {
            Vector3 local = transform.InverseTransformPoint(pos);
            return Mathf.Abs(local.x) < fatalFunnelWidth * 0.5f
                && local.z > -0.5f && local.z < 1.5f;
        }

        /// <summary>Distance from unit to the nearest stack position.</summary>
        public float DistToStack(Vector3 pos)
            => Mathf.Min(
                Vector3.Distance(pos, StackLeftPos),
                Vector3.Distance(pos, StackRightPos));

        // ---------- Registration ---------------------------------------------

        private void OnEnable() => EntryPointRegistry.Register(this);
        private void OnDisable() => EntryPointRegistry.Unregister(this);

        // ---------- Gizmos ---------------------------------------------------

        private void OnDrawGizmos()
        {
#if UNITY_EDITOR
            if (!isBreachable) return;

            Vector3 p = transform.position;

            // Fatal funnel zone
            Gizmos.color = new Color(1f, 0.2f, 0.1f, 0.15f);
            Gizmos.DrawCube(p + transform.forward * 0.5f,
                new Vector3(fatalFunnelWidth, 2f, 1.5f));

            // Stack positions
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.8f);
            Gizmos.DrawSphere(StackLeftPos, 0.2f);
            Gizmos.DrawSphere(StackRightPos, 0.2f);

            // Dom points
            Gizmos.color = new Color(0.1f, 0.9f, 0.4f, 0.8f);
            Gizmos.DrawSphere(DomPosA, 0.2f);
            Gizmos.DrawSphere(DomPosB, 0.2f);

            // Lines from door to dom points
            Gizmos.color = new Color(0.1f, 0.9f, 0.4f, 0.3f);
            Gizmos.DrawLine(p, DomPosA);
            Gizmos.DrawLine(p, DomPosB);

            UnityEditor.Handles.Label(p + Vector3.up * 0.3f,
                leadsToRoomID != "" ? leadsToRoomID : "EntryPoint",
                UnityEditor.EditorStyles.miniLabel);
#endif
        }
    }
}