using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Dashboard.Web.Data;

/// <summary>Used by `dotnet ef` so migrations don't boot the whole app.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<DashboardDbContext>
{
    public DashboardDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<DashboardDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
