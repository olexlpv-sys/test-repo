using Microsoft.Data.SqlClient;

namespace DocHub.Database.Tests;

internal static class SqlAssert
{
    public const int UniqueIndexViolation = 2601;
    public const int UniqueConstraintViolation = 2627;
    public const int ConstraintViolation = 547; // foreign key or check constraint

    /// <summary>Asserts that <paramref name="action"/> fails because of the named constraint or index.</summary>
    public static async Task ViolatesAsync(string constraintName, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<SqlException>(action);

        Assert.Contains(exception.Number, new[] { UniqueIndexViolation, UniqueConstraintViolation, ConstraintViolation });
        Assert.Contains(constraintName, exception.Message, StringComparison.Ordinal);
    }
}
