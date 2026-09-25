using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;

namespace DocHub.Api.Tests;

/// <summary>ProblemDetails conventions (NFR-3), optimistic concurrency (NFR-4), validation and JSON conventions (T04 §4).</summary>
public sealed class ErrorHandlingTests(DocHubApiWithTestEndpointsFactory factory) : IClassFixture<DocHubApiWithTestEndpointsFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("validation", HttpStatusCode.BadRequest, "validation-failed")]
    [InlineData("not-found", HttpStatusCode.NotFound, "not-found")]
    [InlineData("forbidden", HttpStatusCode.Forbidden, "forbidden")]
    [InlineData("conflict", HttpStatusCode.Conflict, "version-not-editable")]
    public async Task Domain_errors_become_problems_with_stable_types(string kind, HttpStatusCode status, string type)
    {
        var problem = await SendAsync(HttpMethod.Post, $"/__test/errors/{kind}", null, status);

        Assert.Equal(type, problem.GetProperty("type").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Mapped_errors_keep_their_status_when_the_client_accepts_only_xml()
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/xml");

        using var response = await client.PostAsync(new Uri("/__test/errors/conflict", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("version-not-editable", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unhandled_errors_are_500_problems_without_internal_details()
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);

        using var response = await client.PostAsync(new Uri("/__test/errors/unexpected", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("internal-error", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret internal detail", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_row_version_returns_409_concurrency_conflict()
    {
        var created = await SendAsync(HttpMethod.Post, "/__test/folders", new { name = "Concurrency" }, HttpStatusCode.OK);
        var id = created.GetProperty("id").GetInt32();
        var original = created.GetProperty("rowVersion").GetString();

        await SendAsync(HttpMethod.Put, $"/__test/folders/{id}", new { name = "First", rowVersion = original }, HttpStatusCode.OK);
        var problem = await SendAsync(HttpMethod.Put, $"/__test/folders/{id}", new { name = "Second", rowVersion = original }, HttpStatusCode.Conflict);

        Assert.Equal("concurrency-conflict", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unique_index_violation_returns_409_with_a_meaningful_type()
    {
        await SendAsync(HttpMethod.Post, "/__test/folders", new { name = "Duplicate me" }, HttpStatusCode.OK);

        var problem = await SendAsync(HttpMethod.Post, "/__test/folders", new { name = "DUPLICATE ME" }, HttpStatusCode.Conflict);

        Assert.Equal("duplicate-name", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Invalid_body_returns_400_validation_failed_with_field_errors()
    {
        var problem = await SendAsync(HttpMethod.Post, "/__test/folders", new { name = "" }, HttpStatusCode.BadRequest);

        Assert.Equal("validation-failed", problem.GetProperty("type").GetString());
        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Theory]
    [InlineData("?page=0", HttpStatusCode.BadRequest)]
    [InlineData("?pageSize=201", HttpStatusCode.BadRequest)]
    [InlineData("?page=2&pageSize=10", HttpStatusCode.OK)]
    [InlineData("", HttpStatusCode.OK)]
    public async Task Paging_parameters_are_validated(string query, HttpStatusCode expected)
    {
        var body = await SendAsync(HttpMethod.Get, $"/__test/paging{query}", null, expected);

        if (expected == HttpStatusCode.OK)
        {
            Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
            Assert.Equal(2, body.GetProperty("items").GetArrayLength());
            Assert.Equal(query.Length == 0 ? 50 : 10, body.GetProperty("pageSize").GetInt32());
        }
    }

    [Fact]
    public async Task Enums_are_serialized_as_strings_and_properties_as_camel_case()
    {
        var body = await SendAsync(HttpMethod.Get, "/__test/enum", null, HttpStatusCode.OK);

        Assert.Equal("Signed", body.GetProperty("status").GetString());
        Assert.Equal("Approver", body.GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/nothing-here", null, HttpStatusCode.NotFound, "not-found")]
    [InlineData("GET", "/api/nothing-here", "text/html", HttpStatusCode.NotFound, "not-found")]
    [InlineData("POST", "/api/me", null, HttpStatusCode.MethodNotAllowed, "method-not-allowed")]
    [InlineData("POST", "/api/me", "application/xml", HttpStatusCode.MethodNotAllowed, "method-not-allowed")]
    [InlineData("POST", "/__test/errors/conflict", "text/html", HttpStatusCode.Conflict, "version-not-editable")]
    public async Task Every_problem_has_type_title_and_trace_id_whatever_the_accept_header(string method, string path, string? accept, HttpStatusCode status, string type)
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);
        if (accept is not null)
        {
            client.DefaultRequestHeaders.Accept.ParseAdd(accept);
        }

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)), Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(type, problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var client = factory.CreateClientFor(TestUsers.Alice);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == expected, $"Expected {(int)expected}, got {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}
