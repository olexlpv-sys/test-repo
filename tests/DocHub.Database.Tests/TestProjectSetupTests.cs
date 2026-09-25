using DocHub.Testing;

namespace DocHub.Database.Tests;

// Placeholder until T02 adds the SQL Server container fixture and schema tests.
public sealed class TestProjectSetupTests
{
    [Fact]
    public void Shared_test_infrastructure_is_referenced()
    {
        Assert.Equal("Performance", TestCategories.Performance);
    }
}
