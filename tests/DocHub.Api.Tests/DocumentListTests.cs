using System.Globalization;
using System.Net;
using System.Text.Json;
using DocHub.Api.Tests.Infrastructure;
using DocHub.Domain.Entities;
using DocHub.Infrastructure.Persistence;
using DocHub.Testing.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocHub.Api.Tests;

/// <summary>
/// T07 / FR-D5: the document list (usp_ListDocuments) equals an EF-built reference query for sorting, paging, the title
/// filter, the status filter and sub-folders — the procedure's contract.
/// </summary>
public sealed class DocumentListTests(DocumentListTests.Data data) : IClassFixture<DocumentListTests.Data>
{
    public static TheoryData<string> Queries() =>
    [
        "",
        "?sortBy=title&sortDir=desc",
        "?sortBy=status",
        "?sortBy=createdAt&sortDir=desc",
        "?sortBy=owner",
        "?sortBy=latestSignedVersion&sortDir=desc",
        "?sortBy=modifiedAt",
        "?page=2&pageSize=4",
        "?page=3&pageSize=4&sortBy=title&sortDir=desc",
        "?status=Signed",
        "?status=Draft&includeSubfolders=true",
        "?includeSubfolders=true&pageSize=100",
        "?search=report",
        "?search=REP",
        "?search=%25",
        "?includeDeleted=true",
        "?includeDeleted=true&status=Deleted&includeSubfolders=true",
    ];

    [Theory]
    [MemberData(nameof(Queries))]
    public async Task The_list_equals_the_reference_query(string query)
    {
        foreach (var user in new[] { TestUsers.Alice, TestUsers.Bob, TestUsers.Admin })
        {
            var actual = await ApiClient.ExpectAsync(data, user, HttpMethod.Get, $"/api/folders/{data.Root}/documents{query}", null, HttpStatusCode.OK);
            var expected = await ReferenceAsync(user, query);

            Assert.Equal(expected.Total, actual.GetProperty("totalCount").GetInt32());
            Assert.Equal(expected.Ids, actual.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList());
        }
    }

    [Fact]
    public async Task The_title_filter_is_case_and_accent_insensitive()
    {
        var hits = await ApiClient.ExpectAsync(data, TestUsers.Bob, HttpMethod.Get, $"/api/folders/{data.Root}/documents?search=CAFE&includeSubfolders=true", null, HttpStatusCode.OK);

        Assert.Equal(["Café menu", "cafe notes"], hits.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Rows_carry_owner_roles_versions_and_signature_progress()
    {
        var list = await ApiClient.ExpectAsync(data, TestUsers.Carol, HttpMethod.Get, $"/api/folders/{data.Root}/documents?pageSize=100", null, HttpStatusCode.OK);
        var signedWithDraft = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == data.SignedWithDraft);

        Assert.Equal(1, signedWithDraft.GetProperty("latestSignedVersion").GetInt32());
        Assert.True(signedWithDraft.GetProperty("hasDraft").GetBoolean());
        Assert.Equal("Draft", signedWithDraft.GetProperty("status").GetString());
        Assert.Equal(["Approver"], signedWithDraft.GetProperty("myRoles").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(TestUsers.Alice, signedWithDraft.GetProperty("owner").GetProperty("id").GetInt32());
        Assert.Equal(0, signedWithDraft.GetProperty("signatureProgress").GetProperty("signed").GetInt32());
        Assert.Equal(1, signedWithDraft.GetProperty("signatureProgress").GetProperty("required").GetInt32());
    }

    [Theory]
    [InlineData("?sortBy=unknown")]
    [InlineData("?status=Other")]
    [InlineData("?pageSize=201")]
    public async Task Invalid_list_parameters_are_400(string query) =>
        await ApiClient.ExpectAsync(data, TestUsers.Bob, HttpMethod.Get, $"/api/folders/{data.Root}/documents{query}", null, HttpStatusCode.BadRequest);

    [Fact]
    public async Task Listing_an_unknown_folder_is_404() =>
        await ApiClient.ExpectAsync(data, TestUsers.Bob, HttpMethod.Get, "/api/folders/999999/documents", null, HttpStatusCode.NotFound);

    /// <summary>The same list, built with EF and LINQ from the documented rules.</summary>
    private async Task<(int Total, List<int> Ids)> ReferenceAsync(int userId, string query)
    {
        var q = System.Web.HttpUtility.ParseQueryString(query);
        var includeDeleted = q["includeDeleted"] == "true";
        var includeSubfolders = q["includeSubfolders"] == "true";
        var search = q["search"];
        var status = q["status"];
        var sortBy = q["sortBy"] ?? "title";
        var desc = q["sortDir"] == "desc";
        var page = int.Parse(q["page"] ?? "1", CultureInfo.InvariantCulture);
        var pageSize = int.Parse(q["pageSize"] ?? "50", CultureInfo.InvariantCulture);

        using var scope = data.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocHubDbContext>();
        var isAdmin = await db.Users.Where(u => u.Id == userId).Select(u => u.IsAdmin).SingleAsync();
        var folders = new HashSet<int> { data.Root };
        if (includeSubfolders)
        {
            var all = await db.Folders.AsNoTracking().ToListAsync();
            for (var added = true; added;)
            {
                added = false;
                foreach (var f in all.Where(f => f.ParentFolderId is { } p && folders.Contains(p) && !folders.Contains(f.Id)))
                {
                    added = folders.Add(f.Id) || added;
                }
            }
        }

        var documents = await db.Documents.AsNoTracking().Where(d => folders.Contains(d.FolderId)).ToListAsync();
        var versions = await db.DocumentVersions.AsNoTracking().Where(v => documents.Select(d => d.Id).Contains(v.DocumentId)).ToListAsync();
        var stamps = await db.VersionStamps.AsNoTracking().ToDictionaryAsync(s => s.DocumentVersionId);
        var owners = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.DisplayName);
        var compare = CultureInfo.InvariantCulture.CompareInfo;

        var rows = documents
            .Where(d => d.DeletedAt is null || (includeDeleted && (isAdmin || d.OwnerUserId == userId)))
            .Where(d => string.IsNullOrWhiteSpace(search) || compare.IndexOf(d.Title, search.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .Select(d =>
            {
                var mine = versions.Where(v => v.DocumentId == d.Id).ToList();
                var current = mine.SingleOrDefault(v => v.IsCurrent);
                var derived = d.DeletedAt is not null ? "Deleted" : mine.Any(v => v.Status == VersionStatus.Draft) ? "Draft" : "Signed";
                var modified = current is not null && stamps.TryGetValue(current.Id, out var s) && s.LastChangedAt is { } changed ? changed : current?.CreatedAt ?? d.CreatedAt;
                return new { d.Id, d.Title, Status = derived, d.CreatedAt, Owner = owners[d.OwnerUserId], Latest = mine.Where(v => v.VersionNumber != null).Max(v => v.VersionNumber), Modified = modified };
            })
            .Where(r => status is null || r.Status == status)
            .ToList();

        // Like the database collation (case-insensitive; accents only break ties); ties are broken by id like the procedure.
        var text = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
        IOrderedEnumerable<T> Order<T, TKey>(IEnumerable<T> source, Func<T, TKey> key, IComparer<TKey>? comparer = null) =>
            desc ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);
        var sorted = sortBy switch
        {
            "status" => Order(rows, r => r.Status, text),
            "createdAt" => Order(rows, r => r.CreatedAt),
            "owner" => Order(rows, r => r.Owner, text),
            "latestSignedVersion" => Order(rows, r => r.Latest),
            "modifiedAt" => Order(rows, r => r.Modified),
            _ => Order(rows, r => r.Title, text),
        };
        var ids = sorted.ThenBy(r => r.Id).Select(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return (rows.Count, ids);
    }

    /// <summary>A folder with a sub-folder and documents of every status, several owners and titles.</summary>
    public sealed class Data(SqlServerContainerFixture server) : DocHubApiFactory(server), IAsyncLifetime
    {
        public int Root { get; private set; }

        public int SignedWithDraft { get; private set; }

        async ValueTask IAsyncLifetime.InitializeAsync()
        {
            await InitializeAsync();
            Root = (await ApiClient.ExpectAsync(this, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { name = "List root" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var sub = (await ApiClient.ExpectAsync(this, TestUsers.Admin, HttpMethod.Post, "/api/folders", new { parentFolderId = Root, name = "Sub" }, HttpStatusCode.Created)).GetProperty("id").GetInt32();
            var arrange = new DocumentArrange(this);

            var titles = new[] { "Annual report", "budget plan", "Contract A", "delta memo", "Expense report", "Forecast", "guide", "Handbook", "Index", "journal" };
            for (var i = 0; i < titles.Length; i++)
            {
                await arrange.CreateAsync(i % 2 == 0 ? TestUsers.Alice : TestUsers.Bob, i < 7 ? Root : sub, titles[i]);
            }

            var (signed, v1, _) = await arrange.SignedAsync();
            await MoveAsync(signed, Root, "Kilo signed");
            var (withDraft, _, _) = await arrange.SignedAsync();
            await MoveAsync(withDraft, Root, "Lima signed with draft");
            await ApiClient.ExpectAsync(this, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{withDraft}/drafts", null, HttpStatusCode.Created);
            await arrange.RevokeAsync(withDraft, TestUsers.Dave);
            SignedWithDraft = withDraft;

            foreach (var (owner, folder, title) in new[] { (TestUsers.Alice, Root, "Mike deleted"), (TestUsers.Bob, sub, "November deleted") })
            {
                var (id, _) = await arrange.CreateAsync(owner, folder, title);
                await ApiClient.ExpectAsync(this, owner, HttpMethod.Delete, $"/api/documents/{id}?rowVersion={Uri.EscapeDataString(await arrange.RowVersionAsync(id))}", null, HttpStatusCode.NoContent);
            }

            await arrange.CreateAsync(TestUsers.Carol, Root, "Café menu");
            await arrange.CreateAsync(TestUsers.Carol, sub, "cafe notes");
            Assert.NotEqual(0, v1);
        }

        private async Task MoveAsync(int documentId, int folderId, string title)
        {
            var arrange = new DocumentArrange(this);
            await ApiClient.ExpectAsync(this, TestUsers.Alice, HttpMethod.Put, $"/api/documents/{documentId}", new { title, rowVersion = await arrange.RowVersionAsync(documentId) }, HttpStatusCode.OK);
            await ApiClient.ExpectAsync(this, TestUsers.Alice, HttpMethod.Post, $"/api/documents/{documentId}/move", new { folderId, rowVersion = await arrange.RowVersionAsync(documentId) }, HttpStatusCode.OK);
        }
    }
}
