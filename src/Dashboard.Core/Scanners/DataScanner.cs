using System.Text.RegularExpressions;

namespace Dashboard.Core.Scanners;

internal static partial class DataScanner
{
    [GeneratedRegex(@"\bclass\s+(\w+)(?:\s*<[^>{]*>)?(?:\s*\([^)]*\))?\s*:\s*(?:[\w.]+\.)?(?:\w*DbContext\b)", RegexOptions.Multiline)]
    private static partial Regex DbContextClass();

    [GeneratedRegex(@"^\s*using\s+Dapper\s*;|\.(Query|QueryAsync|QueryFirstOrDefaultAsync|QuerySingleAsync|ExecuteAsync|QueryMultipleAsync)\s*<", RegexOptions.Multiline)]
    private static partial Regex DapperUsage();

    [GeneratedRegex(@"^V\d+(?:[._]\d+)*__.+\.sql$", RegexOptions.IgnoreCase)]
    private static partial Regex FlywayMigrationName();

    /// <summary>Package id prefix → engine. First match wins.</summary>
    private static readonly (string Prefix, string Engine)[] EnginePackages =
    [
        ("Microsoft.EntityFrameworkCore.SqlServer", "sqlserver"), ("Microsoft.Data.SqlClient", "sqlserver"),
        ("System.Data.SqlClient", "sqlserver"), ("dbup-sqlserver", "sqlserver"), ("dbup-sqlazure", "sqlserver"),
        ("Npgsql", "postgres"), ("dbup-postgresql", "postgres"),
        ("Microsoft.EntityFrameworkCore.Cosmos", "cosmos"), ("Microsoft.Azure.Cosmos", "cosmos"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "sqlite"), ("Microsoft.Data.Sqlite", "sqlite"),
        ("Pomelo.EntityFrameworkCore.MySql", "mysql"), ("MySqlConnector", "mysql"), ("MySql.Data", "mysql"), ("dbup-mysql", "mysql"),
        ("Oracle.ManagedDataAccess", "oracle"), ("Oracle.EntityFrameworkCore", "oracle"),
        ("MongoDB.Driver", "mongodb"), ("MongoDB.EntityFrameworkCore", "mongodb"),
    ];

    public static void Scan(ScanContext ctx, RepoFacts facts, ProjectIndex projects)
    {
        var data = facts.Data;
        var packageIds = facts.Dotnet.Projects.SelectMany(p => p.PackageReferences).Select(p => p.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        data.UsesEfCore = packageIds.Any(id => id.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
        data.UsesDapper = packageIds.Any(id => id.StartsWith("Dapper", StringComparison.OrdinalIgnoreCase));
        data.UsesSqlClient = packageIds.Any(id => id.Equals("Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase)
            || id.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase));
        data.UsesDbUp = packageIds.Any(id => id.StartsWith("dbup", StringComparison.OrdinalIgnoreCase));
        data.UsesFlyway = ctx.Files.Any(f => f.Name.Equals("flyway.conf", StringComparison.OrdinalIgnoreCase)
                || f.Name.Equals("flyway.toml", StringComparison.OrdinalIgnoreCase) || FlywayMigrationName().IsMatch(f.Name))
            || ctx.Files.Where(f => f.Extension is ".yml" or ".yaml" || DeployScanner.IsDockerfile(f))
                .Any(f => ctx.ReadText(f)?.Contains("flyway", StringComparison.OrdinalIgnoreCase) == true);

        data.SqlProjects = facts.Dotnet.Projects.Where(p => p.Kind == "Database").Select(p => p.Path).ToList();

        data.MigrationFolders = ctx.Files
            .Select(f => f.Directory)
            .Where(dir => dir.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Where(dir =>
            {
                var leaf = dir[(dir.LastIndexOf('/') + 1)..];
                return leaf.Equals("Migrations", StringComparison.OrdinalIgnoreCase)
                    || dir.EndsWith("db/migration", StringComparison.OrdinalIgnoreCase);
            })
            .Order(StringComparer.Ordinal).ToList();

        var contexts = new SortedSet<string>(StringComparer.Ordinal);
        var dataProjects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ctx.WithExtension(".cs"))
        {
            var text = ctx.ReadText(f);
            if (text is null) continue;
            var isDataAccess = false;
            if (text.Contains("DbContext", StringComparison.Ordinal))
            {
                foreach (Match m in DbContextClass().Matches(text))
                {
                    contexts.Add(m.Groups[1].Value);
                    isDataAccess = true;
                }
            }
            if (!isDataAccess && (text.Contains("Dapper", StringComparison.Ordinal) || text.Contains("Query", StringComparison.Ordinal))
                && DapperUsage().IsMatch(text) && (text.Contains("using Dapper", StringComparison.Ordinal) || data.UsesDapper))
                isDataAccess = true;
            if (!isDataAccess && text.Contains("new SqlConnection(", StringComparison.Ordinal)) isDataAccess = true;
            if (isDataAccess && projects.OwnerOf(f) is { } owner && owner.Kind != "Database") dataProjects.Add(owner.Name);
        }
        data.DbContexts = contexts.ToList();
        data.DataAccessProjects = dataProjects.ToList();

        var engines = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var id in packageIds)
        {
            foreach (var (prefix, engine) in EnginePackages)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { engines.Add(engine); break; }
            }
        }
        if (data.SqlProjects.Count > 0) engines.Add("sqlserver");
        data.DatabaseEngines = engines.ToList();
    }
}

/// <summary>Maps files to the .NET project whose directory contains them (nearest project wins).</summary>
internal sealed class ProjectIndex
{
    private readonly List<(string Dir, DotnetProject Project)> _byDir;

    public ProjectIndex(IEnumerable<DotnetProject> projects)
    {
        _byDir = projects.Select(p =>
            {
                var i = p.Path.LastIndexOf('/');
                return (Dir: i < 0 ? "" : p.Path[..i], Project: p);
            })
            .OrderByDescending(x => x.Dir.Length).ToList();
    }

    public DotnetProject? OwnerOf(RepoFile file)
    {
        foreach (var (dir, project) in _byDir)
        {
            if (dir.Length == 0 || file.RelativePath.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)) return project;
        }
        return null;
    }
}
