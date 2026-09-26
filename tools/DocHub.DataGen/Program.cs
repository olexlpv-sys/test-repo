using System.Globalization;
using DocHub.DataGen;

// dotnet run --project tools/DocHub.DataGen -- --connection "<connection string>" [--scale 0.1] [--seed 42]
var arguments = args.Select((value, index) => (value, index)).Where(a => a.value.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(a => a.value[2..], a => a.index + 1 < args.Length ? args[a.index + 1] : "", StringComparer.OrdinalIgnoreCase);
var connection = arguments.GetValueOrDefault("connection") ?? Environment.GetEnvironmentVariable("DOCHUB_DATAGEN_CONNECTION");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Usage: DocHub.DataGen --connection \"<SQL connection string of a freshly deployed DocHub database>\" [--scale 0.1] [--seed 42]");
    return 2;
}

var options = new DataGenOptions
{
    Scale = arguments.TryGetValue("scale", out var scale) ? double.Parse(scale, CultureInfo.InvariantCulture) : 0.1,
    Seed = arguments.TryGetValue("seed", out var seed) ? int.Parse(seed, CultureInfo.InvariantCulture) : 42,
};
var report = await new DataGenerator(connection, options, message => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {message}")).RunAsync();
Console.WriteLine(
    $"Generated {report.Users} users, {report.Folders} folders, {report.Documents} documents, {report.Versions} versions, {report.Nodes} nodes, " +
    $"{report.Contents} contents, {report.Grants} grants, {report.Signatures} signatures, {report.Comments} comments, {report.AuditRows} audit rows in {report.Elapsed:mm\\:ss}.");
return 0;
