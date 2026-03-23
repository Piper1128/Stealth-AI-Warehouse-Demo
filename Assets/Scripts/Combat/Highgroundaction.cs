using UnityEngine;
using UnityEngine.AI;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Move to an elevated position with LOS to threat.
    /// Used when squad strategy is Overwatch and unit role is Suppressor.
    /// Requires NavMesh links between floors to be baked correctly.
    /// </summary>
    public class HighGroundAction : GoapAction
    {
        public override string Name => "HighGround";
        public override FormationType PreferredFormation => FormationType.None;
        public override bool IsInterruptible => true;
        public override int Priority => 4;

        private Vector3 _dest;
        private bool _destSet;
        private float _pathCheckTimer;
        private bool _pathBlocked;

        public override bool CheckPreconditions(WorldState s)
            => s.HighGroundNearby
            && s.ThreatConfidence > 0.2f
            && s.SquadStrength > 0.2f;

        public override WorldState ApplyEffects(WorldState s)
        {
            s.HighGroundNearby = true;
            s.HasLOS = true;
            return s;
        }

        public override float GetCost(WorldState s, StealthHuntAI unit)
            => 2.5f; // moderate -- only used when Overwatch strategy active

        public override void OnEnter(StealthHuntAI unit, ThreatModel threat)
        {
            _destSet = false;
            _pathCheckTimer = 0f;
            _pathBlocked = false;

            // Find elevated position via VantageProvider
            var ctx = TacticalContext.Build(unit, threat,
                TacticalBrain.GetOrCreate(unit.squadID));
            var provider = new VantageProvider();
            var spots = provider.GetSpots(ctx);

            if (spots.Count > 0)
            {
                // Pick highest spot that is reachable
                float bestHeight = float.MinValue;
                foreach (var spot in spots)
                {
                    if (spot.Height <= bestHeight) continue;
                    var path = new NavMeshPath();
                    if (!NavMesh.CalculatePath(unit.transform.position,
                        spot.Position, NavMesh.AllAreas, path)) continue;
                    if (path.status != NavMeshPathStatus.PathComplete) continue;
                    bestHeight = spot.Height;
                    _dest = spot.Position;
                    _destSet = true;
                }
            }
        }

        public override bool Execute(StealthHuntAI unit, ThreatModel threat,
                                      TacticalBrain brain, float dt)
        {
            if (!_destSet) return true; // no vantage found

            // Path check every 0.5s
            _pathCheckTimer += dt;
            if (_pathCheckTimer >= 0.5f)
            {
                _pathCheckTimer = 0f;
                var path = new NavMeshPath();
                _pathBlocked = !NavMesh.CalculatePath(unit.transform.position,
                    _dest, NavMesh.AllAreas, path)
                    || path.status != NavMeshPathStatus.PathComplete;
            }
            if (_pathBlocked) return true;

            float dist = Vector3.Distance(unit.transform.position, _dest);
            unit.CombatMoveTo(_dest);
            unit.CombatRestoreRotation();

            // Shoot while moving if LOS
            if (threat.HasLOS) FireAt(unit, threat.EstimatedPosition);

            // Arrived -- face threat and hold
            if (dist < 1.5f)
            {
                unit.CombatStop();
                FaceToward(unit, GetBestKnownPosition(unit, threat));
                if (threat.HasLOS) FireAt(unit, threat.EstimatedPosition);
            }

            return false; // hold position until interrupted
        }
    }
}