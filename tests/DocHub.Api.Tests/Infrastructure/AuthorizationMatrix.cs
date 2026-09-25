using System.Net;
using System.Net.Http.Json;

namespace DocHub.Api.Tests.Infrastructure;

/// <summary>One cell of the authorization matrix: a request made as a user (or anonymously) and the expected status.</summary>
public sealed record AccessCase(string Method, string Path, int? UserId, HttpStatusCode Expected, object? Body = null)
{
    public override string ToString() => $"{Method} {Path} as {(UserId is null ? "anonymous" : $"user {UserId}")} → {(int)Expected}";
}

/// <summary>
/// Table-driven authorization checks (testing strategy: every endpoint × every role). Feature tasks add their rows;
/// all mismatches are reported together.
/// </summary>
public static class AuthorizationMatrix
{
    public static async Task AssertAsync(DocHubApiFactory factory, IEnumerable<AccessCase> cases, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(cases);
        var failures = new List<string>();
        foreach (var access in cases)
        {
            using var client = factory.CreateClientFor(access.UserId);
            using var request = new HttpRequestMessage(new HttpMethod(access.Method), new Uri(access.Path, UriKind.Relative));
            if (access.Body is not null)
            {
                request.Content = JsonContent.Create(access.Body);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != access.Expected)
            {
                failures.Add($"{access}: got {(int)response.StatusCode}");
            }
        }

        Assert.True(failures.Count == 0, "Authorization matrix mismatches:\n" + string.Join('\n', failures));
    }
}
