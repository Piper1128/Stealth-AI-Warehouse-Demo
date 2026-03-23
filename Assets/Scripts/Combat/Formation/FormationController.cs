using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Computes world-space formation positions for squad members.
    /// Each formation type defines slot offsets relative to a leader unit.
    ///
    /// Used by BuddySystem and GoapActions to position units during movement.
    /// Slots are sampled onto NavMesh so they are always reachable.
    /// </summary>
    public class FormationController
    {
        // ---------- Slot offsets per formation type --------------------------

        private static readonly Dictionary<FormationType, Vector3[]> _offsets
            = new Dictionary<FormationType, Vector3[]>
            {
                // Wedge -- leader front, two flankers behind-left/right
                [FormationType.Wedge] = new[]
            {
                new Vector3( 0f, 0f,  0f),   // leader
                new Vector3(-3f, 0f, -3f),   // left flank
                new Vector3( 3f, 0f, -3f),   // right flank
                new Vector3(-5f, 0f, -6f),   // left rear
                new Vector3( 5f, 0f, -6f),   // right rear
            },

                // Line -- side by side
                [FormationType.Line] = new[]
            {
                new Vector3( 0f, 0f, 0f),
                new Vector3(-3f, 0f, 0f),
                new Vector3( 3f, 0f, 0f),
                new Vector3(-6f, 0f, 0f),
                new Vector3( 6f, 0f, 0f),
            },

                // File -- single file behind leader
                [FormationType.File] = new[]
            {
                new Vector3(0f, 0f,  0f),
                new Vector3(0f, 0f, -3f),
                new Vector3(0f, 0f, -6f),
                new Vector3(0f, 0f, -9f),
                new Vector3(0f, 0f,-12f),
            },

                // Diamond -- leader, two sides, one rear
                [FormationType.Diamond] = new[]
            {
                new Vector3( 0f, 0f,  2f),   // front
                new Vector3(-3f, 0f,  0f),   // left
                new Vector3( 3f, 0f,  0f),   // right
                new Vector3( 0f, 0f, -3f),   // rear
            },

                // Vee -- two forward, one rear
                [FormationType.Vee] = new[]
            {
                new Vector3(-3f, 0f, 2f),    // left forward
                new Vector3( 3f, 0f, 2f),    // right forward
                new Vector3( 0f, 0f, 0f),    // rear center
                new Vector3(-5f, 0f, 0f),    // left rear
                new Vector3( 5f, 0f, 0f),    // right rear
            },

                // Overwatch -- one advances, one covers from behind
                [FormationType.Overwatch] = new[]
            {
                new Vector3(0f, 0f, 0f),     // advancer
                new Vector3(3f, 0f, -5f),    // coverer (offset right and back)
            },

                // None -- no formation
                [FormationType.None] = new[]
            {
                new Vector3(0f, 0f, 0f),
            },
            };

        // ---------- Assignment -----------------------------------------------

        private readonly List<FormationSlot> _slots = new List<FormationSlot>();
        private StealthHuntAI _leader;
        private FormationType _formation;

        public void SetFormation(FormationType type, StealthHuntAI leader,
                                  List<StealthHuntAI> members)
        {
            _formation = type;
            _leader = leader;
            _slots.Clear();

            var offsets = GetOffsets(type);

            for (int i = 0; i < members.Count; i++)
            {
                _slots.Add(new FormationSlot
                {
                    AssignedUnit = members[i],
                    LocalOffset = i < offsets.Length ? offsets[i] : offsets[offsets.Length - 1],
                    SlotIndex = i,
                });
            }
        }

        // ---------- Position queries -----------------------------------------

        /// <summary>Get the world position this unit should move to in formation.</summary>
        public Vector3? GetSlotPosition(StealthHuntAI unit)
        {
            if (_leader == null) return null;

            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].AssignedUnit != unit) continue;
                return ComputeWorldPos(_slots[i].LocalOffset);
            }

            return null;
        }

        /// <summary>
        /// Static helper -- get formation slot position without controller.
        /// Used by BuddySystem for quick two-unit positioning.
        /// </summary>
        public static Vector3? GetBuddySlotPosition(StealthHuntAI leader,
                                                      StealthHuntAI follower,
                                                      FormationType type)
        {
            var offsets = GetOffsets(type);
            if (offsets.Length < 2) return null;

            // Follower gets slot 1 (leader gets slot 0)
            Vector3 localOffset = offsets[1];
            Vector3 worldPos = leader.transform.position
                + leader.transform.right * localOffset.x
                + leader.transform.up * localOffset.y
                + leader.transform.forward * localOffset.z;

            // Sample onto NavMesh
            if (NavMesh.SamplePosition(worldPos, out NavMeshHit hit, 3f,
                NavMesh.AllAreas))
                return hit.position;

            return worldPos;
        }

        // ---------- Helpers --------------------------------------------------

        private Vector3 ComputeWorldPos(Vector3 localOffset)
        {
            if (_leader == null) return Vector3.zero;

            Vector3 worldPos = _leader.transform.position
                + _leader.transform.right * localOffset.x
                + _leader.transform.up * localOffset.y
                + _leader.transform.forward * localOffset.z;

            if (NavMesh.SamplePosition(worldPos, out NavMeshHit hit, 3f,
                NavMesh.AllAreas))
                return hit.position;

            return worldPos;
        }

        private static Vector3[] GetOffsets(FormationType type)
        {
            if (_offsets.TryGetValue(type, out var off)) return off;
            return _offsets[FormationType.None];
        }

        // ---------- Debug gizmos (Editor only) -------------------------------

        public void DrawGizmos()
        {
#if UNITY_EDITOR
            if (_leader == null) return;
            var offsets = GetOffsets(_formation);
            for (int i = 0; i < _slots.Count; i++)
            {
                Vector3 pos = ComputeWorldPos(_slots[i].LocalOffset);
                UnityEditor.Handles.color = i == 0
                    ? new Color(1f, 0.8f, 0.1f, 0.8f)
                    : new Color(0.3f, 0.8f, 1f, 0.6f);
                UnityEditor.Handles.DrawWireDisc(pos, Vector3.up, 0.4f);
                UnityEditor.Handles.Label(pos + Vector3.up * 0.3f,
                    "Slot " + i, UnityEditor.EditorStyles.miniLabel);
            }
#endif
        }
    }
}