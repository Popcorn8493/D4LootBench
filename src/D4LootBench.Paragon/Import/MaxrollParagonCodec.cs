using System.Text.Json;
using System.Text.Json.Serialization;
using D4LootBench.Paragon.Models;
using D4LootBench.Paragon.Solver;

namespace D4LootBench.Paragon.Import;

/// <summary>
/// Maxroll's D4 planner "paragon variant code": plain JSON — an array of board objects
/// <c>{id, nodes, rotation, position, glyph?, glyphLevel?}</c>. Node keys are decimal strings of
/// <c>row * 21 + col</c> in the board's UNROTATED local grid; <c>rotation</c> is 0–3 clockwise
/// quarter-turns; <c>position</c> is a board-grid cell with +y pointing down and the starter at (0,0).
/// </summary>
public static class MaxrollParagonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IReadOnlyList<MaxrollBoardEntry> Decode(string code)
    {
        List<MaxrollBoardEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<MaxrollBoardEntry>>(code, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a Maxroll paragon variant code (invalid JSON): {ex.Message}");
        }
        if (entries is null || entries.Count == 0)
            throw new FormatException("Not a Maxroll paragon variant code (empty).");
        if (entries.Any(e => string.IsNullOrEmpty(e.Id)))
            throw new FormatException("Not a Maxroll paragon variant code (board entry without an id).");
        return entries;
    }

    public static string Encode(IReadOnlyList<MaxrollBoardEntry> entries) =>
        JsonSerializer.Serialize(entries, Options);

    /// <summary>
    /// Converts decoded entries into a layout plus the allocated cells in display coordinates.
    /// Maxroll stores absolute board positions; the attachment tree is reconstructed by walking
    /// outward from the starter at (0,0).
    /// </summary>
    public static ConvertedMaxrollBuild ToLayout(
        IReadOnlyList<MaxrollBoardEntry> entries,
        IReadOnlyDictionary<string, ParagonBoardDef> boardsByInternalName)
    {
        var byPosition = new Dictionary<(int X, int Y), MaxrollBoardEntry>();
        foreach (var entry in entries)
        {
            var key = (entry.Position?.X ?? 0, entry.Position?.Y ?? 0);
            if (!byPosition.TryAdd(key, entry))
                throw new FormatException($"Two boards occupy position ({key.Item1}, {key.Item2}).");
        }
        if (!byPosition.ContainsKey((0, 0)))
            throw new FormatException("No starting board at position (0, 0).");

        // Walk outward from the starter along GATE connectivity: the starter has exactly one
        // gate (Top before rotation), every other board has gates on all four edges. A board
        // can sit beside the starter's gateless edges — its real parent is another neighbor.
        var deltas = new (int Dx, int Dy, BoardEdge Edge)[]
        {
            (0, -1, BoardEdge.Top), (0, 1, BoardEdge.Bottom), (-1, 0, BoardEdge.Left), (1, 0, BoardEdge.Right),
        };
        var starterGateEdge = RotateEdge(BoardEdge.Top, byPosition[(0, 0)].Rotation);

        var placed = new List<PlacedBoard>();
        var entryBySlot = new List<MaxrollBoardEntry>();
        var slotByPosition = new Dictionary<(int X, int Y), int>();

        var starterEntry = byPosition[(0, 0)];
        placed.Add(new PlacedBoard
        {
            Board = ResolveBoard(starterEntry.Id, boardsByInternalName),
            RotationSteps = starterEntry.Rotation & 3,
        });
        entryBySlot.Add(starterEntry);
        slotByPosition[(0, 0)] = 0;

        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((0, 0));
        while (queue.Count > 0)
        {
            var pos = queue.Dequeue();
            int parentSlot = slotByPosition[pos];
            foreach (var (dx, dy, edge) in deltas)
            {
                if (pos == (0, 0) && edge != starterGateEdge)
                    continue; // the starter's other edges have no gate
                var childPos = (pos.X + dx, pos.Y + dy);
                if (!byPosition.TryGetValue(childPos, out var childEntry) || slotByPosition.ContainsKey(childPos))
                    continue;

                slotByPosition[childPos] = placed.Count;
                placed.Add(new PlacedBoard
                {
                    Board = ResolveBoard(childEntry.Id, boardsByInternalName),
                    RotationSteps = childEntry.Rotation & 3,
                    ParentSlot = parentSlot,
                    AttachEdge = edge,
                });
                entryBySlot.Add(childEntry);
                queue.Enqueue(childPos);
            }
        }

        if (placed.Count != entries.Count)
            throw new FormatException("Boards are not all connected to the starting board through gates.");

        var layout = new ParagonLayout(placed);

        var allocated = new List<CellRef>();
        var glyphs = new List<MaxrollGlyphAssignment>();
        for (int slot = 0; slot < placed.Count; slot++)
        {
            var entry = entryBySlot[slot];
            int width = placed[slot].Board.Width;
            if (entry.Nodes is not null)
            {
                foreach (var key in entry.Nodes.Keys)
                {
                    if (!int.TryParse(key, out int index) || index < 0 || index >= width * width)
                        continue;
                    var (x, y) = ParagonLayout.Rotate(index % width, index / width, placed[slot].RotationSteps, width);
                    allocated.Add(new CellRef(slot, x, y));
                }
            }
            if (!string.IsNullOrEmpty(entry.Glyph))
                glyphs.Add(new MaxrollGlyphAssignment(slot, entry.Glyph, entry.GlyphLevel));
        }

        return new ConvertedMaxrollBuild(layout, allocated, glyphs);
    }

    /// <summary>Rotating a board 90° clockwise moves its Top edge content to the Right edge.</summary>
    private static BoardEdge RotateEdge(BoardEdge edge, int rotationSteps)
    {
        ReadOnlySpan<BoardEdge> clockwise = [BoardEdge.Top, BoardEdge.Right, BoardEdge.Bottom, BoardEdge.Left];
        int index = clockwise.IndexOf(edge);
        return clockwise[(index + rotationSteps) & 3];
    }

    /// <summary>
    /// Builds entries from a layout and allocated display cells (include the start and any
    /// gate cells the path purchases — Maxroll expects the full allocation).
    /// </summary>
    public static IReadOnlyList<MaxrollBoardEntry> FromLayout(
        ParagonLayout layout,
        IReadOnlyCollection<CellRef> allocatedDisplayCells,
        IReadOnlyCollection<MaxrollGlyphAssignment>? glyphs = null)
    {
        var entries = new List<MaxrollBoardEntry>();
        for (int slot = 0; slot < layout.Boards.Count; slot++)
        {
            var placed = layout.Boards[slot];
            int width = placed.Board.Width;
            int inverseSteps = (4 - placed.RotationSteps) & 3;

            var nodes = new SortedDictionary<int, int>();
            foreach (var cell in allocatedDisplayCells)
            {
                if (cell.BoardSlot != slot)
                    continue;
                var (x, y) = ParagonLayout.Rotate(cell.X, cell.Y, inverseSteps, width);
                nodes[y * width + x] = 1;
            }

            var glyph = glyphs?.FirstOrDefault(g => g.BoardSlot == slot);
            entries.Add(new MaxrollBoardEntry
            {
                Id = placed.Board.InternalName,
                Nodes = nodes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
                Rotation = placed.RotationSteps,
                Position = new MaxrollPosition
                {
                    X = layout.BoardPositions[slot].X,
                    Y = layout.BoardPositions[slot].Y,
                },
                Glyph = glyph?.GlyphInternalName,
                GlyphLevel = glyph?.Level,
            });
        }
        return entries;
    }

    private static ParagonBoardDef ResolveBoard(
        string id, IReadOnlyDictionary<string, ParagonBoardDef> boardsByInternalName)
    {
        if (!boardsByInternalName.TryGetValue(id, out var board))
            throw new FormatException($"Unknown paragon board id '{id}'.");
        return board;
    }
}

public sealed class MaxrollBoardEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("nodes")]
    public Dictionary<string, int>? Nodes { get; set; }

    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    [JsonPropertyName("position")]
    public MaxrollPosition? Position { get; set; }

    [JsonPropertyName("glyph")]
    public string? Glyph { get; set; }

    [JsonPropertyName("glyphLevel")]
    public int? GlyphLevel { get; set; }
}

public sealed class MaxrollPosition
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}

public sealed record MaxrollGlyphAssignment(int BoardSlot, string GlyphInternalName, int? Level);

public sealed record ConvertedMaxrollBuild(
    ParagonLayout Layout,
    IReadOnlyList<CellRef> AllocatedCells,
    IReadOnlyList<MaxrollGlyphAssignment> Glyphs);
