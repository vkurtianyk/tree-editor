using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TreeEditor.Data;

/// <summary>
/// Design-time only (`dotnet ef migrations add`); never connects.
/// At runtime the context is registered by Aspire (<c>AddNpgsqlDbContext</c>).
/// </summary>
public sealed class TreeDbContextFactory : IDesignTimeDbContextFactory<TreeDbContext>
{
    public TreeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TreeDbContext>()
            .UseNpgsql("Host=localhost;Database=treedb")
            .Options);
}
