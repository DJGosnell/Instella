using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Instella.Server.Data;

/// <summary>
/// Lets <c>dotnet ef</c> build the model without running the server's startup code. Migrations are
/// provider-specific (SQLite); the connection string is never opened for <c>migrations add</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
