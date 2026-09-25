using DocHub.Domain.Ordering;

namespace DocHub.Api.Tests;

/// <summary>Gap-based sort orders (ADR-02).</summary>
public sealed class SiblingOrderTests
{
    [Theory]
    [InlineData(new int[0], null, 1024)]
    [InlineData(new[] { 1024, 2048 }, null, 3072)]
    [InlineData(new[] { 1024, 2048 }, 0, 512)]
    [InlineData(new[] { 1024, 2048 }, 1, 1536)]
    [InlineData(new[] { 1024, 2048 }, 99, 3072)]
    public void Uses_the_middle_of_the_gap(int[] siblings, int? position, int expected)
    {
        var (sortOrder, renumbered) = SiblingOrder.Place(siblings, position);

        Assert.Equal(expected, sortOrder);
        Assert.Null(renumbered);
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3 }, 1)]
    [InlineData(new[] { 5, 5 }, 1)]
    [InlineData(new[] { 1 }, 0)]
    [InlineData(new[] { -10, 3 }, 0)]
    public void Renumbers_when_no_gap_is_left(int[] siblings, int position)
    {
        var (sortOrder, renumbered) = SiblingOrder.Place(siblings, position);

        Assert.NotNull(renumbered);
        var all = renumbered.ToList();
        all.Insert(position, sortOrder);
        Assert.Equal(all.Order(), all);
        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void Renumbers_near_the_upper_limit() =>
        Assert.NotNull(SiblingOrder.Place([int.MaxValue - 1], null).Renumbered);
}
