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
            recorder.Add("new draft", i * 30, 201);
            recorder.Add("tree", 100, 200);
        }

        recorder.Add("tree", 50_000, 0); // a failure counts as a failure, not in the budget
        var budgets = LoadBudgets.Evaluate(recorder, new ProcedureProbe("unused", Catalog));

        var draft = Assert.Single(budgets, b => b.Operation.Contains("[new draft]", StringComparison.Ordinal));
        Assert.Equal((2850, false), (draft.MeasuredP95, draft.Met));
        Assert.True(Assert.Single(budgets, b => b.Operation.Contains("[tree]", StringComparison.Ordinal)).Met);
        // Not measured is not met.
        Assert.False(Assert.Single(budgets, b => b.Operation.Contains("usp_CheckPermission", StringComparison.Ordinal)).Met);
    }

    [Fact]
    public async Task Memory_growth_is_the_trend_after_the_warm_up()
    {
        var clock = new LoadRecorder();
        long value = 1_000;
        var growing = new MemorySampler(() => value += 20);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await growing.RunAsync(clock, TimeSpan.FromMilliseconds(20), stop.Token);

        Assert.True(growing.GrowthAfter(TimeSpan.Zero) > 0.10);
        var flat = new MemorySampler(() => 1_000);
        using var stop2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await flat.RunAsync(clock, TimeSpan.FromMilliseconds(20), stop2.Token);
        Assert.Equal(0, flat.GrowthAfter(TimeSpan.Zero), 3);
    }
}
