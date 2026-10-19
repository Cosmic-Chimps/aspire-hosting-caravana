using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// "cache" is the Redis resource of the AppHost. Aspire passes its connection string (password
// included) as ConnectionStrings__cache; on Caravana it arrives as a secret.
builder.AddRedisClient("cache");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapGet("/", async (IConnectionMultiplexer redis) =>
{
    var visits = await redis.GetDatabase().StringIncrementAsync("visits");
    return Results.Text($"Hello from Caravana. Visits: {visits}");
});

app.Run();
