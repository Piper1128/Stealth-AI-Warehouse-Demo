using System.Collections.Generic;
using UnityEngine;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Animation triggers for the Combat Pack.
    /// Used by StandardCombat to play animations independently of Core AnimSlots.
    /// </summary>
    public enum CombatAnimTrigger
    {
        // Movement
        MoveToCover,
        TakeCover,
        CoverIdle,
        Reposition,
        Advance,
        Flank,
        Vault,
        Sprint,

        // Engagement
        PeekLeft,
        PeekRight,
        CoverFire,
        Suppressing,
        StandingFire,
        KneelingFire,

        // Prone
        GoProne,
        ProneIdle,
        ProneFire,
        GetUp,

        // Grenades
        ThrowGrenade,
        ThrowGrenadeOver,

        // Reactions
        HitReaction,
        Stagger,
        TakeCoverReaction,

        // Utilities
        Reload,
        Melee,

        // Custom -- call PlayCombatAnim("name") from code
        Custom
    }

    /// <summary>
    /// Maps a CombatAnimTrigger to one or more Animator clip names.
    /// Multiple clips = random pick each time state is entered.
    /// Extra keywords allow users to extend auto-assign matching.
    /// </summary>
    [System.Serializable]
    public class CombatAnimSlot
    {
        public CombatAnimTrigger trigger = CombatAnimTrigger.MoveToCover;
        public List<string> clips = new List<string>();
        public string customName = "";

        [Tooltip("Extra keywords for Auto Assign matching. Case insensitive.")]
        public List<string> extraKeywords = new List<string>();

        /// <summary>Pick a random clip. Returns null if none assigned.</summary>
        public string Pick()
        {
            if (clips == null || clips.Count == 0) return null;
            var valid = clips.FindAll(c => !string.IsNullOrEmpty(c));
            if (valid.Count == 0) return null;
            return valid.Count == 1
                ? valid[0]
                : valid[UnityEngine.Random.Range(0, valid.Count)];
        }
    }
}