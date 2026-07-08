namespace D4LootBench.Paragon.Solver;

public sealed class SolverResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }

    /// <summary>Cells to purchase, excluding the free start node. Connected and covering all targets.</summary>
    public IReadOnlyList<CellRef> PurchasedCells { get; init; } = [];

    /// <summary>Paragon points required (one per purchased cell).</summary>
    public int PointsSpent => PurchasedCells.Count;

    /// <summary>True when the exact solver ran; false for the heuristic used above the target cap.</summary>
    public bool IsOptimal { get; init; }

    public static SolverResult Failed(string error) => new() { Success = false, Error = error };
}
