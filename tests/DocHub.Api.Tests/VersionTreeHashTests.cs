using DocHub.Domain.Content;
using DocHub.Domain.Signing;

namespace DocHub.Api.Tests;

/// <summary>The content hash signatures are bound to (T07 rule 2).</summary>
public sealed class VersionTreeHashTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");

    [Fact]
    public void Canonical_json_ignores_key_order_and_whitespace_but_not_values()
    {
        Assert.Equal(CanonicalJson.Hash("""{"b":1,"a":{"y":[1,2],"x":"t"}}"""), CanonicalJson.Hash("""{ "a": { "x": "t", "y": [ 1, 2 ] }, "b": 1 }"""));
        Assert.NotEqual(CanonicalJson.Hash("""{"a":[1,2]}"""), CanonicalJson.Hash("""{"a":[2,1]}"""));
        Assert.NotEqual(CanonicalJson.Hash("""{"a":"x"}"""), CanonicalJson.Hash("""{"a":"X"}"""));
    }

    [Fact]
    public void Tree_hash_is_independent_of_row_order_and_database_ids()
    {
        var tree = Tree(1);
        var shuffled = Enumerable.Reverse(Tree(100)).ToList();

        Assert.Equal(VersionTreeHash.Compute(tree), VersionTreeHash.Compute(shuffled));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("content")]
    [InlineData("type")]
    [InlineData("order")]
    [InlineData("parent")]
    public void Every_signed_aspect_changes_the_hash(string change)
    {
        var original = VersionTreeHash.Compute(Tree(1));
        var nodes = Tree(1).ToList();
        var section = nodes[1]; // child of Chapter
        nodes[1] = change switch
        {
            "title" => section with { Title = "Other" },
            "content" => section with { ContentJson = """{"type":"doc","x":2}""" },
            "type" => section with { NodeTypeId = 9 },
            "order" => nodes[2] with { SortOrder = 1 },
            _ => section with { ParentId = null },
        };
        if (change == "order")
        {
            (nodes[1], nodes[2]) = (section, nodes[1]);
        }

        Assert.NotEqual(original, VersionTreeHash.Compute(nodes));
    }

    [Fact]
    public void Titles_with_separators_cannot_collide()
    {
        var one = new[] { new TreeHashNode(1, null, A, 1, "x|y", 1, null) };
        var two = new[] { new TreeHashNode(1, null, A, 1, "x", 1, null) with { Title = "x|y" } };

        Assert.Equal(VersionTreeHash.Compute(one), VersionTreeHash.Compute(two));
        Assert.NotEqual(VersionTreeHash.Compute([new TreeHashNode(1, null, A, 1, "a|1|b", 1, null)]), VersionTreeHash.Compute([new TreeHashNode(1, null, A, 1, "a", 1, null)]));
    }

    private static List<TreeHashNode> Tree(int idBase) =>
    [
        new(idBase, null, A, 1, "Chapter", 1024, """{"type":"doc"}"""),
        new(idBase + 1, idBase, B, 2, "Section", 1024, """{ "type" : "doc" }"""),
        new(idBase + 2, null, C, 1, "Annex", 2048, """{"type":"doc","x":1}"""),
    ];
}
