using DocHub.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddDocHubApi();

var app = builder.Build();
app.UseDocHubApi();
app.Run();

/// <summary>Entry point; public for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
