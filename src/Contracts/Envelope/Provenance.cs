namespace SignalRouter.Contracts
{
    /// <summary>
    /// "Directed by whom?" — one of the four orthogonal identity-envelope fields
    /// (semantic-model.md §6). Closed vocabulary; honest uncertainty keeps
    /// <see cref="Unknown"/> first-class.
    /// </summary>
    public enum Provenance
    {
        HumanDirected,
        Automation,
        Unknown,
    }
}
