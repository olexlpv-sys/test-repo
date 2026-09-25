namespace DocHub.Api.Endpoints;

/// <summary>A feature area's endpoints (Minimal API group). Registered in DI and mapped at startup.</summary>
public interface IEndpointModule
{
    void Map(IEndpointRouteBuilder endpoints);
}
