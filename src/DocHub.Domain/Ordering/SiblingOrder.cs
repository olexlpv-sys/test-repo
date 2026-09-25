namespace DocHub.Domain.Ordering;

/// <summary>
/// Sort orders with gaps (ADR-02): siblings are 1024, 2048, …; an item placed between two siblings takes the middle of the
/// gap, and the sibling set is renumbered only when there is no gap left.
/// </summary>
public static class SiblingOrder
{
    public const int Gap = 1024;

    /// <summary>
    /// The sort order for an item inserted at <paramref name="position"/> (0-based, clamped; <c>null</c> = last) among
    /// <paramref name="siblings"/> (the other items, in display order). When no gap is left, <c>Renumbered</c> holds new
    /// sort orders for all siblings (same order as given) and the returned value fits into them.
    /// </summary>
    public static (int SortOrder, IReadOnlyList<int>? Renumbered) Place(IReadOnlyList<int> siblings, int? position)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        var index = Math.Clamp(position ?? siblings.Count, 0, siblings.Count);
        long before = index == 0 ? 0 : siblings[index - 1];
        long after = index == siblings.Count ? before + 2L * Gap : siblings[index];
        if (after - before >= 2 && before + (after - before) / 2 <= int.MaxValue - Gap)
        {
            return ((int)(before + (after - before) / 2), null);
        }

        // No gap: renumber — slots 1..n+1 get multiples of the gap, the new item takes slot index + 1.
        var renumbered = new List<int>(siblings.Count);
        for (var i = 0; i < siblings.Count; i++)
        {
            renumbered.Add((i < index ? i + 1 : i + 2) * Gap);
        }

        var sortOrder = (index + 1) * Gap;
        return (sortOrder, renumbered);
    }
}
