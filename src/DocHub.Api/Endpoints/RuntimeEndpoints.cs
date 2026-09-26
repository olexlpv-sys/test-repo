using System.Diagnostics;
using DocHub.Api.Auth;

namespace DocHub.Api.Endpoints;

/// <summary>
/// Admin → runtime sample of the answering API instance (T19 soak: "no memory growth trend in the API"). Behind a load balancer
/// each call reaches one instance; the instance id lets the soak follow every instance's trend.
/// </summary>
internal sealed class RuntimeEndpoints : IEndpointModule
{
    private static readonly string Instance = $"{Environment.MachineName}/{Environment.ProcessId}";

    public void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/runtime", () =>
            {
                using var process = Process.GetCurrentProcess();
                return TypedResults.Ok(new RuntimeSample(Instance, process.WorkingSet64, GC.GetGCMemoryInfo().HeapSizeBytes, process.StartTime.ToUniversalTime()));
            })
            .RequireAuthorization(AuthPolicies.Admin)
            .WithTags("Admin")
            .WithName("GetRuntimeSample")
            .WithSummary("Admin: memory of the answering API instance (working set, managed heap after the last GC) for soak tests.");
    }

    public sealed record RuntimeSample(string Instance, long WorkingSetBytes, long ManagedHeapBytes, DateTime StartedAt);
}
