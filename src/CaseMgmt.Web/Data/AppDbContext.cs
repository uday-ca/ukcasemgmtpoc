using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CaseMgmt.Web.Data;

/// <summary>One page visit - the smallest table that proves migrations (deploy identity) and reads/writes (app identity).</summary>
public class Visit
{
    public int Id { get; set; }
    public DateTime VisitedUtc { get; set; }
    public string Slot { get; set; } = "";
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Visit> Visits => Set<Visit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Visit>(e =>
        {
            e.Property(v => v.Slot).HasMaxLength(100);
            e.HasIndex(v => v.VisitedUtc);
        });
    }
}

/// <summary>
/// Used only by the EF Core tools (migrations, migration bundle) - they never start the web app.
/// The pipeline passes the real connection with --connection.
/// </summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=CaseMgmtDesign;Trusted_Connection=True")
            .Options);
}
