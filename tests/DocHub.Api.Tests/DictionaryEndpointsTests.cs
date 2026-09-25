using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Testing.Database;

namespace DocHub.Api.Tests;

/// <summary>T05: users (read-only), node types and the content style catalog through the API (connecting as app_api).</summary>
public sealed class DictionaryEndpointsTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Users ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Users_are_listed_active_only_paged_and_searchable_by_prefix()
    {
        var all = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users?pageSize=100", null, HttpStatusCode.OK);
        var ids = all.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("id").GetInt32()).ToList();
        Assert.DoesNotContain(TestUsers.System, ids); // inactive
        Assert.Contains(TestUsers.Carol, ids);
        Assert.Equal(ids.Count, all.GetProperty("totalCount").GetInt32());

        var byLogin = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users?search=car", null, HttpStatusCode.OK);
        var carol = Assert.Single(byLogin.GetProperty("items").EnumerateArray());
        Assert.Equal(TestUsers.Carol, carol.GetProperty("id").GetInt32());
        Assert.Equal("carol", carol.GetProperty("login").GetString());

        var page = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users?page=2&pageSize=2", null, HttpStatusCode.OK);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("page").GetInt32());

        // LIKE wildcards are literal.
        var wildcard = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users?search=%25", null, HttpStatusCode.OK);
        Assert.Equal(0, wildcard.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("/api/users?pageSize=101")]
    [InlineData("/api/users?page=0")]
    public async Task User_paging_is_validated(string path) =>
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, path, null, HttpStatusCode.BadRequest);

    [Fact]
    public async Task A_user_is_read_by_id_including_inactive_ones()
    {
        var system = await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users/0", null, HttpStatusCode.OK);
        Assert.False(system.GetProperty("isActive").GetBoolean());
        await ApiClient.ExpectAsync(factory, TestUsers.Alice, HttpMethod.Get, "/api/users/999999", null, HttpStatusCode.NotFound);
    }

    // ---- Node types -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Node_type_crud_by_admin_is_audited()
    {
        var created = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types",
            new { code = "ANNEX_A", name = "Annex", description = "Appendix", sortOrder = 90 }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("ANNEX_A", created.GetProperty("code").GetString());
        Assert.Equal(0, created.GetProperty("usageCount").GetInt32());

        var read = await ApiClient.ExpectAsync(factory, TestUsers.Carol, HttpMethod.Get, $"/api/node-types/{id}", null, HttpStatusCode.OK);
        Assert.Equal("Annex", read.GetProperty("name").GetString());

        var updated = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{id}",
            new { code = "ANNEX_B", name = "Annex B", sortOrder = 91, isActive = true, rowVersion = read.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        Assert.Equal("ANNEX_B", updated.GetProperty("code").GetString());

        // Stale rowVersion → 409 concurrency-conflict.
        var stale = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{id}",
            new { code = "ANNEX_C", name = "Annex C", sortOrder = 1, isActive = true, rowVersion = read.GetProperty("rowVersion").GetString() }, HttpStatusCode.Conflict);
        Assert.Equal("concurrency-conflict", stale.GetProperty("type").GetString());

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/api/node-types/{id}", null, HttpStatusCode.NoContent);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/node-types/{id}", null, HttpStatusCode.NotFound);

        var log = await Db.QueryAsync(factory,
            "SELECT Operation, Source, UserId FROM audit.ChangeLog WHERE TableName = N'app.NodeType' AND EntityId = @id ORDER BY Id;", ("@id", id));
        Assert.Equal(["I", "U", "D"], log.Select(r => (string)r["Operation"]!));
        Assert.All(log, r => Assert.Equal("App", r["Source"]));
        Assert.All(log, r => Assert.Equal(TestUsers.Admin, r["UserId"]));
    }

    [Fact]
    public async Task Node_types_are_listed_in_order_and_inactive_ones_only_on_request()
    {
        var created = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types",
            new { code = "RETIRED", name = "Retired", sortOrder = -5 }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt32();
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{id}",
            new { code = "RETIRED", name = "Retired", sortOrder = -5, isActive = false, rowVersion = created.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);

        var active = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/node-types", null, HttpStatusCode.OK);
        Assert.DoesNotContain(active.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);
        var sort = active.EnumerateArray().Select(t => (t.GetProperty("sortOrder").GetInt32(), t.GetProperty("name").GetString()!)).ToList();
        Assert.Equal(sort.OrderBy(s => s.Item1).ThenBy(s => s.Item2, StringComparer.OrdinalIgnoreCase), sort);

        var all = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/node-types?includeInactive=true", null, HttpStatusCode.OK);
        Assert.Equal(id, all.EnumerateArray().First().GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData("chapter")]
    [InlineData("A")]
    [InlineData("1ABC")]
    [InlineData("AB-C")]
    public async Task Invalid_code_is_a_field_error(string code)
    {
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types", new { code, name = "X" }, HttpStatusCode.BadRequest);

        Assert.Equal("validation-failed", problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("code", out _));
    }

    [Fact]
    public async Task Duplicate_code_is_409()
    {
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types", new { code = "CHAPTER", name = "Again" }, HttpStatusCode.Conflict);

        Assert.Equal("duplicate-name", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_used_node_type_cannot_be_deleted_but_can_be_deactivated()
    {
        var created = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/node-types", new { code = "USED_TYPE", name = "Used" }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt32();
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            var version = await dbo.Data.VersionAsync(await dbo.Data.DocumentAsync());
            await dbo.ExecuteAsync(
                """
                INSERT INTO app.DocumentNode (DocumentVersionId, LogicalNodeId, NodeTypeId, Title, SortOrder, CreatedByUserId, ModifiedByUserId)
                VALUES (@v, NEWID(), @t, N'Uses the type', 1024, 2, 2);
                """,
                ("@v", version), ("@t", id));
        }

        var read = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, $"/api/node-types/{id}", null, HttpStatusCode.OK);
        Assert.Equal(1, read.GetProperty("usageCount").GetInt32());

        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/api/node-types/{id}", null, HttpStatusCode.Conflict);
        Assert.Equal("in-use", problem.GetProperty("type").GetString());

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/node-types/{id}",
            new { code = "USED_TYPE", name = "Used", sortOrder = 0, isActive = false, rowVersion = read.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        var list = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/node-types", null, HttpStatusCode.OK);
        Assert.DoesNotContain(list.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415_unsupported_media_type()
    {
        using var client = factory.CreateClientFor(TestUsers.Admin);
        using var content = new StringContent("code=X", System.Text.Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync(new Uri("/api/node-types", UriKind.Relative), content, Ct);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("unsupported-media-type", problem.GetProperty("type").GetString());
    }

    // ---- Content styles ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Content_style_crud_by_admin_is_audited()
    {
        var created = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "Warning", name = "Warning", kind = "Paragraph", basedOnStyleId = "Normal", properties = new { color = "#C00000", bold = true } }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetInt32();
        Assert.Equal("Paragraph", created.GetProperty("kind").GetString());
        Assert.True(created.GetProperty("properties").GetProperty("bold").GetBoolean());
        Assert.False(created.GetProperty("isBuiltIn").GetBoolean());

        var updated = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/content-styles/{id}",
            new { name = "Warning box", basedOnStyleId = "Normal", properties = new { color = "#FF0000" }, isActive = true, rowVersion = created.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);
        Assert.Equal("Warning box", updated.GetProperty("name").GetString());
        Assert.False(updated.GetProperty("properties").TryGetProperty("bold", out _));

        var paragraphs = await ApiClient.ExpectAsync(factory, TestUsers.Bob, HttpMethod.Get, "/api/content-styles?kind=Paragraph", null, HttpStatusCode.OK);
        Assert.Contains(paragraphs.EnumerateArray(), s => s.GetProperty("styleId").GetString() == "Warning");
        Assert.All(paragraphs.EnumerateArray(), s => Assert.Equal("Paragraph", s.GetProperty("kind").GetString()));

        var rowVersion = Uri.EscapeDataString(updated.GetProperty("rowVersion").GetString()!);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete, $"/api/content-styles/{id}?rowVersion={rowVersion}", null, HttpStatusCode.NoContent);

        var log = await Db.QueryAsync(factory, "SELECT Operation FROM audit.ChangeLog WHERE TableName = N'app.ContentStyle' AND EntityId = @id ORDER BY Id;", ("@id", id));
        Assert.Equal(["I", "U", "D"], log.Select(r => (string)r["Operation"]!));
    }

    [Fact]
    public async Task Built_in_and_used_styles_cannot_be_deleted()
    {
        var builtIn = await StyleAsync("Quote");
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete,
            $"/api/content-styles/{builtIn.GetProperty("id").GetInt32()}?rowVersion={Uri.EscapeDataString(builtIn.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.Conflict);
        Assert.Equal("built-in-style", problem.GetProperty("type").GetString());

        var custom = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "Highlighted", name = "Highlighted", kind = "Character", properties = new { highlight = "#FFFF00" } }, HttpStatusCode.Created);
        await using (var dbo = await SqlSession.OpenAsync(factory.AdminConnectionString))
        {
            var nodeId = await dbo.Data.NodeAsync(await dbo.Data.VersionAsync(await dbo.Data.DocumentAsync()));
            await dbo.Data.ContentAsync(nodeId);
            await dbo.ExecuteAsync("INSERT INTO app.ContentStyleUsage (StyleId, NodeId) VALUES (N'Highlighted', @n);", ("@n", nodeId));
        }

        var used = await StyleAsync("Highlighted");
        Assert.Equal(1, used.GetProperty("usageCount").GetInt32());
        problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete,
            $"/api/content-styles/{custom.GetProperty("id").GetInt32()}?rowVersion={Uri.EscapeDataString(custom.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.Conflict);
        Assert.Equal("in-use", problem.GetProperty("type").GetString());

        // A base style of other styles is in use too.
        var normal = await StyleAsync("Normal");
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Delete,
            $"/api/content-styles/{normal.GetProperty("id").GetInt32()}?rowVersion={Uri.EscapeDataString(normal.GetProperty("rowVersion").GetString()!)}", null, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Stylesheet_is_anonymous_reflects_changes_and_revalidates_with_etag()
    {
        using var anonymous = factory.CreateClientFor(null);
        using var first = await anonymous.GetAsync(new Uri("/api/content-styles/stylesheet.css", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("text/css", first.Content.Headers.ContentType?.MediaType);
        var css = await first.Content.ReadAsStringAsync(Ct);
        Assert.Contains(".ds-style-Heading1 {", css, StringComparison.Ordinal);
        Assert.Contains("font-size: 20pt", Rule(css, "Heading1"), StringComparison.Ordinal);
        // Inherited from Normal (BasedOn chain merged).
        Assert.Contains("line-height: 1.079", Rule(css, "Heading1"), StringComparison.Ordinal);
        var etag = first.Headers.ETag!;

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/content-styles/stylesheet.css", UriKind.Relative)))
        {
            request.Headers.IfNoneMatch.Add(etag);
            using var cached = await anonymous.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        }

        var heading = await StyleAsync("Heading1");
        var properties = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(heading.GetProperty("properties").GetRawText())!;
        properties["fontSize"] = JsonSerializer.SerializeToElement(48);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/content-styles/{heading.GetProperty("id").GetInt32()}",
            new { name = "Heading 1", basedOnStyleId = "Normal", properties, isActive = true, rowVersion = heading.GetProperty("rowVersion").GetString() }, HttpStatusCode.OK);

        using (var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/content-styles/stylesheet.css", UriKind.Relative)))
        {
            request.Headers.IfNoneMatch.Add(etag);
            using var changed = await anonymous.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            Assert.Contains("font-size: 24pt", Rule(await changed.Content.ReadAsStringAsync(Ct), "Heading1"), StringComparison.Ordinal);
            Assert.NotEqual(etag, changed.Headers.ETag);
        }
    }

    [Fact]
    public async Task Style_properties_and_inheritance_are_validated()
    {
        var problem = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "Bad", name = "Bad", kind = "Character", properties = new { fontSize = 1000, color = "red", fontFamily = "Comic Sans", align = "center", glow = true } },
            HttpStatusCode.BadRequest);
        var errors = problem.GetProperty("errors");
        foreach (var path in new[] { "properties.fontSize", "properties.color", "properties.fontFamily", "properties.align", "properties.glow" })
        {
            Assert.True(errors.TryGetProperty(path, out _), $"missing error for {path}: {errors}");
        }

        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "Orphan", name = "Orphan", kind = "Paragraph", basedOnStyleId = "Missing", properties = new { } }, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "WrongKind", name = "Wrong kind", kind = "Character", basedOnStyleId = "Normal", properties = new { } }, HttpStatusCode.BadRequest);
        await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "No-Dash", name = "x", kind = "Paragraph", properties = new { } }, HttpStatusCode.BadRequest);

        // Cycle: Normal based on Heading1 (which is based on Normal).
        var normal = await StyleAsync("Normal");
        var cycle = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Put, $"/api/content-styles/{normal.GetProperty("id").GetInt32()}",
            new { name = "Normal", basedOnStyleId = "Heading1", properties = new { }, isActive = true, rowVersion = normal.GetProperty("rowVersion").GetString() }, HttpStatusCode.BadRequest);
        Assert.True(cycle.GetProperty("errors").TryGetProperty("basedOnStyleId", out _));

        var duplicate = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Post, "/api/content-styles",
            new { styleId = "Normal", name = "Normal again", kind = "Paragraph", properties = new { } }, HttpStatusCode.Conflict);
        Assert.Equal("duplicate-name", duplicate.GetProperty("type").GetString());
    }

    private async Task<JsonElement> StyleAsync(string styleId)
    {
        var all = await ApiClient.ExpectAsync(factory, TestUsers.Admin, HttpMethod.Get, "/api/content-styles?includeInactive=true", null, HttpStatusCode.OK);
        return all.EnumerateArray().Single(s => s.GetProperty("styleId").GetString() == styleId);
    }

    private static string Rule(string css, string styleId)
    {
        var start = css.IndexOf($".ds-style-{styleId} {{", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no rule for {styleId}");
        return css[start..css.IndexOf('}', start)];
    }
}
