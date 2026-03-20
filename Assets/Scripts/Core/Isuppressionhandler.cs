namespace StealthHuntAI
{
    /// <summary>
    /// Interface for components that handle suppression on a unit.
    /// Allows Core to apply suppression without depending on Demo assembly.
    /// Implement on GuardHealth or any custom health component.
    /// </summary>
    public interface ISuppressionHandler
    {
        void AddSuppression(float amount);
        bool IsSuppressed { get; }
    }
}