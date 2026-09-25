namespace DocHub.Testing;

/// <summary>Paths inside the repository (tests always run from a checkout).</summary>
public static class Repository
{
    public static string Root { get; } = FindRoot();

    public static string DatabaseProject => Path.Combine(Root, "database", "DocHub.Database");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Tests must run from inside the repository.");
    }
}
