using System.Text.Json;
using FieldOps.Api.Models;
using StackExchange.Redis;

namespace FieldOps.Api.Application;

// Day 54: idempotency — if a client retries POST /api/workorders with the
// same Idempotency-Key (e.g. after a timeout where the original response
// never arrived), replay the original result instead of creating a second
// work order. Uses Redis directly (IConnectionMultiplexer, Day 48), same as
// WorkOrderReportService — a short TTL is exactly what an idempotency
// record needs, no need to remember a key forever, just long enough to
// catch a genuine retry.
public class IdempotencyService
{
    private static readonly TimeSpan RecordDuration = TimeSpan.FromSeconds(60);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(IConnectionMultiplexer redis, ILogger<IdempotencyService> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    // Day 51/52's failure-isolation lesson applied a third time: a Redis
    // blip here must never block a genuinely new request. Worst case, a
    // retried request gets processed twice instead of being deduplicated —
    // safe-by-default ("fail open"), not a data-corrupting failure.
    //
    // Day 119: async (StringGetAsync/StringSetAsync) — Create, its only
    // caller, is async now.
    public async Task<WorkOrderDto?> TryGetCachedResponseAsync(string idempotencyKey)
    {
        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync($"idempotency:{idempotencyKey}");
            return cached.HasValue ? JsonSerializer.Deserialize<WorkOrderDto>((string)cached!) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check idempotency cache for key {IdempotencyKey}", idempotencyKey);
            return null;
        }
    }

    public async Task StoreResponseAsync(string idempotencyKey, WorkOrderDto response)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync($"idempotency:{idempotencyKey}", JsonSerializer.Serialize(response), RecordDuration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store idempotency record for key {IdempotencyKey}", idempotencyKey);
        }
    }
}
