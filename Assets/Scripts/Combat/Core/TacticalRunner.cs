using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Drives all squad-level systems every frame independent of individual guards.
    /// Attach to HuntDirector GameObject -- one instance drives all squads.
    /// </summary>
    public class TacticianRunner : MonoBehaviour
    {
        [Header("Squad Coherency")]
        [Tooltip("Max distance a guard can be from squad center before waiting for others.")]
        [Range(5f, 40f)] public float coherencyRadius = 18f;

        private void Update()
        {
            var all = HuntDirector.AllUnits;
            var seen = new System.Collections.Generic.HashSet<int>();

            for (int i = 0; i < all.Count; i++)
            {
                var u = all[i];
                if (u == null || u.IsDead) continue;
                if (u.CurrentAlertState != AlertState.Hostile) continue;
                if (!seen.Add(u.squadID)) continue;

                var brain = TacticalBrain.GetOrCreate(u.squadID);

                // Tick all brain subsystems -- null guard on WorldState
                try
                {
                    brain.CoherencyRadius = coherencyRadius;
                    brain.UpdateSquadAnchor(all, u.squadID);
                    brain.Tactician.Tick(Time.deltaTime, brain, all, u.squadID);
                    brain.TickCommittedGoal();
                    brain.CQB.Tick(Time.deltaTime);

                    if (!u.IsDead) // guard may have died during tick
                    {
                        var ws = WorldState.Build(u, brain.Intel.Threat, brain);
                        brain.Strategy.Update(Time.deltaTime, ws, brain);
                    }

                    // CQB room cleared
                    if (brain.CQB.IsActive && brain.CQB.RoomCleared)
                    {
                        brain.CQB.EndEntry();
                        brain.ClearCommittedGoal();
                        for (int j = 0; j < all.Count; j++)
                        {
                            if (all[j] == null || all[j].IsDead) continue;
                            if (all[j].squadID != u.squadID) continue;
                            var sc = all[j].GetComponent<StandardCombat>();
                            if (sc != null && brain.Intel.Threat.HasIntel
                             && !brain.Intel.Threat.HasLOS)
                                sc.ForceRole(StandardCombat.CombatRole.Cautious);
                        }
                    }
                }
                catch (System.Exception e)
                {
                    UnityEngine.Debug.LogWarning($"[TacticianRunner] Squad {u.squadID}: {e.Message}");
                }
            }
        }
    }
}