using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Snapshot of all context providers and scorers need to do their work.
    /// Built once per tactical request and passed through the pipeline.
    /// Immutable after construction -- never modified by providers or scorers.
    /// </summary>
    public class TacticalContext
    {
        // ---------- Threat ---------------------------------------------------

        /// <summary>Per-guard threat model with estimated position.</summary>
        public ThreatModel Threat;

        /// <summary>Estimated current player position.</summary>
        public Vector3 EstimatedThreatPos;

        /// <summary>Last confirmed player position.</summary>
        public Vector3 LastKnownThreatPos;

        /// <summary>Last known player velocity.</summary>
        public Vector3 ThreatVelocity;

        /// <summary>Confidence in threat position (0-1).</summary>
        public float ThreatConfidence;

        /// <summary>True if guard currently has LOS to player.</summary>
        public bool HasLOS;

        // ---------- Unit -----------------------------------------------------

        /// <summary>The unit making this request.</summary>
        public StealthHuntAI Unit;

        /// <summary>Unit world position at time of request.</summary>
        public Vector3 UnitPosition;

        /// <summary>Unit's current squad role.</summary>
        public SquadRole Role;

        /// <summary>Current awareness level (0-1) from stealth system.</summary>
        public float Awareness;

        // ---------- Squad ----------------------------------------------------

        /// <summary>Shared tactical brain for squad coordination.</summary>
        public TacticalBrain Brain;

        /// <summary>All living squad members including this unit.</summary>
        public List<StealthHuntAI> SquadMembers;

        /// <summary>Positions currently reserved by other squad members.</summary>
        public List<Vector3> ReservedPositions;

        /// <summary>Current squad formation type.</summary>
        public FormationType Formation;

        // ---------- World ----------------------------------------------------

        /// <summary>Max distance to search for spots.</summary>
        public float SearchRadius;

        /// <summary>NavMesh layer mask for path queries.</summary>
        public int NavMeshMask;

        /// <summary>Time of this request (Time.time).</summary>
        public float RequestTime;

        // ---------- Builder --------------------------------------------------

        /// <summary>Build context from a unit and its current threat model.</summary>
        public static TacticalContext Build(StealthHuntAI unit,
                                             ThreatModel threat,
                                             TacticalBrain brain,
                                             float searchRadius = 25f)
        {
            var members = new List<StealthHuntAI>();
            var reserved = new List<Vector3>();

            // Collect squad members and their reserved positions
            var units = HuntDirector.AllUnits;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null) continue;
                if (u.squadID == unit.squadID)
                {
                    members.Add(u);
                    // Collect any reserved spots from other squad members
                    if (u != unit)
                    {
                        var sc = u.GetComponent<StandardCombat>();
                        if (sc?.CurrentSpot != null)
                            reserved.Add(sc.CurrentSpot.Position);
                    }
                }
            }

            return new TacticalContext
            {
                Threat = threat,
                EstimatedThreatPos = threat.HasIntel
                                      ? threat.EstimatedPosition
                                      : unit.transform.position + unit.transform.forward * 10f,
                LastKnownThreatPos = threat.LastKnownPosition,
                ThreatVelocity = threat.LastKnownVelocity,
                ThreatConfidence = threat.Confidence,
                HasLOS = threat.HasLOS,
                Unit = unit,
                UnitPosition = unit.transform.position,
                Role = unit.ActiveRole,
                Awareness = unit.AwarenessLevel,
                Brain = brain,
                SquadMembers = members,
                ReservedPositions = reserved,
                Formation = FormationType.None,
                SearchRadius = searchRadius,
                NavMeshMask = UnityEngine.AI.NavMesh.AllAreas,
                RequestTime = Time.time,
            };
        }
    }
}