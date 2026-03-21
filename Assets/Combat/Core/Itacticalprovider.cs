using System.Collections.Generic;

namespace StealthHuntAI.Combat
{
    /// <summary>
    /// Finds candidate tactical positions for a unit to move to.
    /// Providers are the "eyes" of the tactical system -- they generate
    /// the raw list of spots that scorers then evaluate.
    ///
    /// Providers should be fast -- they run on the main thread.
    /// Heavy work belongs in scorers which run in Burst jobs.
    /// </summary>
    public interface ITacticalProvider
    {
        /// <summary>
        /// Unique tag identifying this provider in debug output.
        /// Example: "CoverProvider", "FlankProvider"
        /// </summary>
        string Tag { get; }

        /// <summary>
        /// Whether this provider is currently active.
        /// Inactive providers are skipped entirely.
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Generate candidate spots given the current tactical context.
        /// Must not modify context. Must not block (no heavy physics queries).
        /// Returns empty list if no valid spots found -- never returns null.
        /// </summary>
        List<TacticalSpot> GetSpots(TacticalContext ctx);
    }
}