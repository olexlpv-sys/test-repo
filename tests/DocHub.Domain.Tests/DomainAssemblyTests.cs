using DocHub.Domain;

namespace DocHub.Domain.Tests;

public sealed class DomainAssemblyTests
{
    [Fact]
    public void Domain_assembly_has_no_infrastructure_dependencies()
    {
        var references = typeof(DomainAssembly).Assembly.GetReferencedAssemblies().Select(a => a.Name);

        Assert.DoesNotContain(references, name => name is not null && (name.StartsWith("DocHub.Infrastructure", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
            || name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)));
    }
}
