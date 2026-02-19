using Microsoft.EntityFrameworkCore;
using Recommendations.Api.Infrastructure.Persistence.Entities;

namespace Recommendations.Api.Infrastructure.Persistence;

public class RecommendationsDbContext : DbContext
{
    public RecommendationsDbContext(DbContextOptions<RecommendationsDbContext> options)
        : base(options) { }

    public DbSet<CachedRecommendation> CachedRecommendations => Set<CachedRecommendation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CachedRecommendation>(e =>
        {
            e.HasIndex(x => x.CacheKey).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
            e.Property(x => x.ResponseJson).HasColumnType("TEXT");
        });
    }
}
