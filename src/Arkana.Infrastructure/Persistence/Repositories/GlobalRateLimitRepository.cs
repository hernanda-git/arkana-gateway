using Arkana.Domain.Entities;
using Arkana.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Arkana.Infrastructure.Persistence.Repositories;

public sealed class GlobalRateLimitRepository : IGlobalRateLimitRepository
{
    private readonly GatewayDbContext _db;

    public GlobalRateLimitRepository(GatewayDbContext db) => _db = db;

    public async Task<GlobalRateLimit?> GetAsync()
        => await _db.GlobalRateLimits.FirstOrDefaultAsync();

    public async Task UpsertAsync(GlobalRateLimit config)
    {
        var existing = await _db.GlobalRateLimits.FirstOrDefaultAsync();
        if (existing is null)
        {
            _db.GlobalRateLimits.Add(config);
        }
        else
        {
            existing.Enabled = config.Enabled;
            existing.DefaultRequestsPerMinute = config.DefaultRequestsPerMinute;
            existing.DefaultTokensPerMinute = config.DefaultTokensPerMinute;
            existing.DefaultMaxConcurrent = config.DefaultMaxConcurrent;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync();
    }
}
