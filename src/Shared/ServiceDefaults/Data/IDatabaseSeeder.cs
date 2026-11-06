using Microsoft.EntityFrameworkCore;

namespace NewStore.ServiceDefaults.Data;

/// <summary>
/// Puts a service's initial data in its database. The seeder decides whether the data is already there,
/// so the "seed" command can run on every start.
/// </summary>
public interface IDatabaseSeeder<in TContext>
    where TContext : DbContext
{
    Task SeedAsync(TContext db, CancellationToken cancellationToken);
}
