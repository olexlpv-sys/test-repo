namespace DocHub.Testing.Database;

/// <summary>Parameterized SQL helpers shared by <see cref="RolledBackScope"/> and <see cref="SqlSession"/>.</summary>
public interface ISqlCommands
{
    Task<int> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters);

    Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] parameters);

    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string sql, params (string Name, object? Value)[] parameters);
}
