using Microsoft.EntityFrameworkCore;

namespace NewStore.Catalog;

/// <summary>Catalog's database. It has no entities yet; the first one arrives with the catalog feature.</summary>
public class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options);
