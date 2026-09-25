using DocHub.Api.Tests.Infrastructure;
using DocHub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DocHub.Api.Tests;

/// <summary>
/// No EF migrations (ADR-01): the EF model must match the schema deployed from the DACPAC — every app table mapped, every
/// visible column of a mapped table mapped, and for every mapped column the same name, store type and nullability.
/// </summary>
public sealed class SchemaDriftTests(DocHubApiFactory factory) : IClassFixture<DocHubApiFactory>
{
    [Fact]
    public async Task Ef_model_matches_the_deployed_database_schema()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<DocHubDbContext>().GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var database = await ReadDatabaseColumnsAsync();

        var modelColumns = model.GetEntityTypes()
            .SelectMany(entity =>
            {
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                return entity.GetProperties().Select(p => new Column(
                    $"{table.Schema}.{table.Name}",
                    p.GetColumnName(table)!,
                    Normalize(p.GetColumnType(table)),
                    p.IsColumnNullable(table)));
            })
            .ToList();

        var problems = new List<string>();
        var mappedTables = modelColumns.Select(c => c.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        problems.AddRange(database.Select(c => c.Table).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => t.StartsWith("app.", StringComparison.OrdinalIgnoreCase) && !mappedTables.Contains(t))
            .Select(t => $"table {t} is not mapped in the EF model"));

        var mappedColumns = modelColumns.Select(c => (c.Table.ToUpperInvariant(), c.Name.ToUpperInvariant())).ToHashSet();
        problems.AddRange(database
            .Where(c => mappedTables.Contains(c.Table) && !mappedColumns.Contains((c.Table.ToUpperInvariant(), c.Name.ToUpperInvariant())))
            .Select(c => $"{c.Table}.{c.Name}: exists in the database but is not mapped in the EF model"));

        var byKey = database.ToDictionary(c => (c.Table.ToUpperInvariant(), c.Name.ToUpperInvariant()));
        foreach (var column in modelColumns)
        {
            if (!byKey.TryGetValue((column.Table.ToUpperInvariant(), column.Name.ToUpperInvariant()), out var actual))
            {
                problems.Add($"{column.Table}.{column.Name}: mapped but missing in the database");
                continue;
            }

            if (!string.Equals(actual.StoreType, column.StoreType, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{column.Table}.{column.Name}: EF type {column.StoreType}, database type {actual.StoreType}");
            }

            if (actual.Nullable != column.Nullable)
            {
                problems.Add($"{column.Table}.{column.Name}: EF nullable={column.Nullable}, database nullable={actual.Nullable}");
            }
        }

        Assert.True(problems.Count == 0, "Schema drift between the EF model and the DACPAC:\n" + string.Join('\n', problems));
    }

    private async Task<List<Column>> ReadDatabaseColumnsAsync()
    {
        var rows = await Db.QueryAsync(factory,
            """
            SELECT TABLE_SCHEMA + '.' + TABLE_NAME AS [Table], COLUMN_NAME AS [Name], DATA_TYPE AS [Type],
                   CHARACTER_MAXIMUM_LENGTH AS [Length], DATETIME_PRECISION AS [Precision], IS_NULLABLE AS [Nullable]
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA IN ('app', 'audit')
              -- Hidden columns (temporal period / ledger columns) are managed by SQL Server and never mapped.
              AND COLUMNPROPERTY(OBJECT_ID(QUOTENAME(TABLE_SCHEMA) + '.' + QUOTENAME(TABLE_NAME)), COLUMN_NAME, 'IsHidden') = 0;
            """);

        return rows.Select(r => new Column(
            (string)r["Table"]!,
            (string)r["Name"]!,
            StoreType((string)r["Type"]!, r["Length"] as int?, r["Precision"] is short p ? p : null),
            (string)r["Nullable"]! == "YES")).ToList();
    }

    private static string StoreType(string type, int? length, short? precision) => type.ToLowerInvariant() switch
    {
        "nvarchar" or "varchar" or "varbinary" or "char" or "nchar" => $"{type}({(length == -1 ? "max" : length)})",
        "datetime2" => $"datetime2({precision})",
        "timestamp" => "rowversion",
        _ => type,
    };

    private static string Normalize(string? efType) => (efType ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private sealed record Column(string Table, string Name, string StoreType, bool Nullable);
}
