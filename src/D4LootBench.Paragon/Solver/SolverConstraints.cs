namespace D4LootBench.Paragon.Solver;

/// <summary>
/// Optional steering for the solver. Weighted cells stay purchasable but cost more to the
/// optimizer, so they are only taken when they save at least that many plain nodes; blocked
/// cells are never traversed at all. <see cref="SolverResult.PointsSpent"/> still counts one
/// point per node — weights shape which tree the solver prefers, not what it reports.
/// </summary>
public sealed class SolverConstraints
{
    public static readonly SolverConstraints None = new();

    /// <summary>Traversal cost per cell; cells not listed cost 1. Values are clamped to ≥ 1.</summary>
    public IReadOnlyDictionary<CellRef, int> CellWeights { get; init; } = new Dictionary<CellRef, int>();

    /// <summary>Cells the solver must never purchase or pass through.</summary>
    public IReadOnlyCollection<CellRef> BlockedCells { get; init; } = [];

    public bool IsEmpty => CellWeights.Count == 0 && BlockedCells.Count == 0;
}
