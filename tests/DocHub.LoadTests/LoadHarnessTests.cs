using System.Net;
using System.Text;

namespace DocHub.LoadTests;

/// <summary>The load harness itself (T19 review): nothing is lost, the mix is per request, the budget and memory checks compute right.</summary>
public sealed class LoadHarnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LoadCatalog Catalog = new(
        [
            new LoadDocument(1, 1, 10, 100, HasDraft: true, LatestSignedVersionId: 99, [11], [12], Nodes(1000)),
            new LoadDocument(2, 1, 10, 200, HasDraft: false, LatestSignedVersionId: 200, [11], [12], Nodes(2000)),
        ],
        [20, 21]);

    private static (int, Guid)[] Nodes(int first) => [.. Enumerable.Range(first, 8).Select(id => (id, Guid.NewGuid()))];

    /// <summary>Answers every API call of the flows with a plausible body; <paramref name="behave"/> can take over a call.</summary>
    private sealed class FakeApi(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>?>? behave = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (behave?.Invoke(request, cancellationToken) is { } special)
            {
                return await special;
            }

            var path = request.RequestUri!.AbsolutePath;
            var body = (request.Method.Method, path) switch
            {
                ("GET", _) when path.EndsWith("/content", StringComparison.Ordinal) => """{"contentJson":{"type":"doc","content":[]},"rowVersion":"AAAAAAAAAAE="}""",
                ("PUT", _) => """{"rowVersion":"AAAAAAAAAAI="}""",
                ("POST", _) when path.EndsWith("/nodes", StringComparison.Ordinal) || path.EndsWith("/drafts", StringComparison.Ordinal) => """{"id":5,"rowVersion":"AAAAAAAAAAM="}""",
                ("POST", _) when path.EndsWith("/pdf", StringComparison.Ordinal) => """{"jobId":1,"status":"Succeeded"}""",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static LoadFlows Flows(HttpMessageHandler handler, LoadRecorder recorder, TimeSpan? timeout = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test") }, Catalog, recorder, exports: true, TimeSpan.Zero, timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task The_request_mix_is_70_20_10_by_requests()
    {
        var recorder = new LoadRecorder();
        var flows = Flows(new FakeApi(), recorder);
        var random = new Random(7);
        for (var i = 0; i < 6000; i++)
        {
            await flows.RunOneAsync(random, Ct);
        }

        var mix = recorder.Mix();
        Assert.InRange(mix[LoadCategory.Reader], 67, 73);
        Assert.InRange(mix[LoadCategory.Editor], 17, 23);
        Assert.InRange(mix[LoadCategory.Other], 8, 12);
        Assert.Contains(recorder.Samples, s => s.Endpoint == "add node");
        Assert.Contains(recorder.Samples, s => s.Endpoint == "new draft");
        // The flow rate conversion matches what the flows really send.
        Assert.InRange(recorder.Samples.Count / 6000.0, LoadFlows.RequestsPerFlow * 0.95, LoadFlows.RequestsPerFlow * 1.05);
    }

    [Fact]
    public async Task A_request_running_when_the_run_ends_is_still_recorded()
    {
        var release = new TaskCompletionSource();
        var recorder = new LoadRecorder();
        var flows = Flows(new FakeApi(async (_, _) =>
        {
            await release.Task;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }), recorder);
        using var run = new CancellationTokenSource();

        var flow = flows.RunOneAsync(new Random(1), run.Token);
        await run.CancelAsync(); // the run ends while the first request hangs
        Assert.False(await recorder.DrainAsync(TimeSpan.FromMilliseconds(200)));
        release.SetResult();

        Assert.True(await recorder.DrainAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow);
        Assert.Contains(recorder.Samples, s => s.Status == 500);
    }

    [Fact]
    public async Task A_request_over_the_timeout_and_an_unreadable_body_are_failures()
    {
        var recorder = new LoadRecorder();
        var calls = 0;
        var flows = Flows(new FakeApi(async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct); // hangs until the request timeout
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };
        }), recorder, TimeSpan.FromMilliseconds(200));

        await flows.RunOneAsync(new Random(1), Ct);

        Assert.True(recorder.Samples.Count >= 2);
        Assert.All(recorder.Samples, s => Assert.Equal(0, s.Status));
        Assert.All(recorder.Stats(), s => Assert.Equal(s.Requests, s.Failures));
    }

    [Fact]
    public void Budgets_compare_the_p95_of_successful_requests_and_of_the_procedures()
    {
        var recorder = new LoadRecorder();
        for (var i = 1; i <= 100; i++)
        {
            recorder.Add(LoadBudgets.LargeNewDraft, i * 30, 201);
            recorder.Add("node content", 100, 200);
        }

        recorder.Add("node content", 50_000, 0); // a failure counts as a failure, not in the budget
        var budgets = LoadBudgets.Evaluate(recorder, new ProcedureProbe("unused", Catalog));

        var draft = Assert.Single(budgets, b => b.Operation.Contains(LoadBudgets.LargeNewDraft, StringComparison.Ordinal));
        Assert.Equal((2850, false), (draft.MeasuredP95, draft.Met));
        Assert.True(Assert.Single(budgets, b => b.Operation.Contains("[node content]", StringComparison.Ordinal)).Met);
        // Not measured is not met.
        Assert.False(Assert.Single(budgets, b => b.Operation.Contains("usp_CheckPermission", StringComparison.Ordinal)).Met);
    }

    [Fact]
    public async Task The_document_size_budgets_are_measured_on_the_largest_documents_only()
    {
        var withDraft = new LoadDocument(7, 1, 10, 700, HasDraft: true, LatestSignedVersionId: 699, [11], [12], Nodes(7000), LoadCatalog.LargeDocumentNodes);
        var withoutDraft = new LoadDocument(8, 1, 10, 800, HasDraft: false, LatestSignedVersionId: 800, [11], [12], Nodes(8000), LoadCatalog.LargeDocumentNodes + 20);
        var catalog = Catalog with { Documents = [.. Catalog.Documents, withDraft, withoutDraft] };
        var recorder = new LoadRecorder();
        // Small documents open fast, but that says nothing about a 2 000-node one.
        recorder.Add("open document", 10, 200);
        recorder.Add("tree", 10, 200);
        Assert.False(Assert.Single(LoadBudgets.Evaluate(recorder, new ProcedureProbe("unused", catalog)), b => b.Operation.Contains(LoadBudgets.LargeOpen, StringComparison.Ordinal)).Met);

        var flows = new LoadFlows(new HttpClient(new FakeApi()) { BaseAddress = new Uri("http://api.test") }, catalog, recorder, exports: false, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        await flows.LargeDocumentAsync(new Random(3), Ct);

        var budgets = LoadBudgets.Evaluate(recorder, new ProcedureProbe("unused", catalog));
        foreach (var operation in new[] { LoadBudgets.LargeOpen, LoadBudgets.LargeHistory, LoadBudgets.LargeCompare, LoadBudgets.LargeNewDraft })
        {
            Assert.True(Assert.Single(budgets, b => b.Operation.Contains(operation, StringComparison.Ordinal)).Samples > 0, operation);
        }

        // Header and tree are one operation: the combined sample is timed across both requests.
        Assert.Equal(2, recorder.Samples.Count(s => s.Endpoint == LoadBudgets.LargeOpen));
        Assert.DoesNotContain(recorder.Samples, s => s.Category == LoadCategory.Probe && s.Endpoint.StartsWith("new draft (", StringComparison.Ordinal) && s.Status == 0);
        Assert.Equal(0, recorder.Mix()[LoadCategory.Reader]); // the probe is outside the mix
    }

    [Fact]
    public async Task Memory_growth_is_the_trend_of_each_instance_after_the_warm_up()
    {
        var clock = new LoadRecorder();
        long growing = 1_000;
        var calls = 0;
        // Two instances behind a load balancer: one flat, one growing — the growing one fails the soak.
        var sampler = new MemorySampler(_ => Task.FromResult(Interlocked.Increment(ref calls) % 2 == 0 ? ("a", 1_000L) : ("b", growing += 20)));
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await sampler.RunAsync(clock, TimeSpan.FromMilliseconds(20), stop.Token);

        Assert.Equal(2, sampler.InstancesWithTrend(TimeSpan.Zero));
        Assert.True(sampler.GrowthAfter(TimeSpan.Zero) > 0.10);
        Assert.Contains("grew", sampler.Verdict(TimeSpan.Zero, 2), StringComparison.Ordinal);
        var flat = new MemorySampler(_ => Task.FromResult(("a", 1_000L)));
        using var stop2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await flat.RunAsync(clock, TimeSpan.FromMilliseconds(20), stop2.Token);
        Assert.Equal(0, flat.GrowthAfter(TimeSpan.Zero), 3);
        Assert.Null(flat.Verdict(TimeSpan.Zero, 1));
        // Behind a sticky load balancer only one of two instances answers: that is not a pass.
        Assert.Contains("1 of 2", flat.Verdict(TimeSpan.Zero, 2), StringComparison.Ordinal);
    }
}
