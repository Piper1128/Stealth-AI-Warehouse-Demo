using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// A single slot in a formation.
    /// Defines the local offset from the formation leader
    /// and which unit is assigned to this slot.
    /// </summary>
    public class FormationSlot
    {
        /// <summary>Slot index -- 0 is always the leader.</summary>
        public int SlotIndex;

        /// <summary>Local offset from leader position (right, up, forward).</summary>
        public Vector3 LocalOffset;

        /// <summary>Unit currently assigned to this slot.</summary>
        public StealthHuntAI AssignedUnit;

        /// <summary>Preferred squad role for this slot.</summary>
        public SquadRole PreferredRole;

        /// <summary>True if a unit is assigned to this slot.</summary>
        public bool IsOccupied => AssignedUnit != null;

        /// <summary>
        /// Compute world-space position of this slot given the leader's transform.
        /// </summary>
        public Vector3 GetWorldPosition(Transform leaderTransform)
        {
            if (leaderTransform == null) return Vector3.zero;

            Vector3 worldPos = leaderTransform.position
                + leaderTransform.right * LocalOffset.x
                + leaderTransform.up * LocalOffset.y
                + leaderTransform.forward * LocalOffset.z;

            // Sample onto NavMesh
            if (UnityEngine.AI.NavMesh.SamplePosition(worldPos, out var hit, 3f,
                UnityEngine.AI.NavMesh.AllAreas))
                return hit.position;

            return worldPos;
        }

        public override string ToString()
            => "Slot[" + SlotIndex + "] offset=" + LocalOffset
             + " unit=" + (AssignedUnit != null ? AssignedUnit.name : "none");
    }
}