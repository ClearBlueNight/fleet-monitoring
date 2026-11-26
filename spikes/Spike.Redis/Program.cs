using System.Globalization;
using StackExchange.Redis;

// Connect
var password = Environment.GetEnvironmentVariable("REDIS_PASSWORD");
if(string.IsNullOrEmpty(password))
{
    Console.WriteLine("Set REDIS_PASSWORD first (same val as in deploy.env).");
    return 1;
}

var options = ConfigurationOptions.Parse("localhost:6379");
options.Password = password;
options.AbortOnConnectFail = false;

using var mux = await ConnectionMultiplexer.ConnectAsync(options);
var db = mux.GetDatabase();
Console.WriteLine($"Connected: {mux.IsConnected}");

// Key naming helpers
const string Company = "11111111-1111-1111-1111-111111111111";
string Key(string suffix) => $"fleet:{Company}:{suffix}";
string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

const string ApplySampleLua = """
    local truckKey      = KEYS[1]
    local geoKey        = KEYS[2]
    local lastSeenKey   = KEYS[3]
    local statusPrefix  = ARGV[1]
    local truckId       = ARGV[2]
    local eventMs       = tonumber(ARGV[3])
    local lat           = ARGV[4]
    local lon           = ARGV[5]
    local speed         = ARGV[6]
    local status        = ARGV[7]

    local prev          = redis.call('HMGET', truckKey, 'last_event_time', 'status')
    local prevMs        = tonumber(prev[1]) or 0
    local prevStatus    = prev[2] or ''

    if eventMs <= prevMs then
        return {0, prevStatus}
    end

    redis.call('HSET', truckKey, 'lat', lat, 'lon', lon, 'speed_kmh', speed, 'status', status, 'last_event_time', ARGV[3])
    redis.call('GEOADD', geoKey, lon, lat, truckId)
    redis.call('ZADD', lastSeenKey, eventMs, truckId)

    if prevStatus ~= status then
        if prevStatus ~= '' then
            redis.call('SREM', statusPrefix .. prevStatus, truckId)
        end
        redis.call('SADD', statusPrefix .. status, truckId)
    end

    return {1, prevStatus}
    """;

// C# wrapper around the script
async Task<(bool Applied, string PrevStatus)> Apply( string truckId, long eventMs, double lat, double lon, double speed, string status)
{
    var reply = (RedisResult[])(await db.ScriptEvaluateAsync(
        ApplySampleLua,
        new RedisKey[] {Key($"truck:{truckId}"), Key("geo"), Key("lastseen")},
        new RedisValue[]
        {
            Key("status:"), truckId, eventMs.ToString(CultureInfo.InvariantCulture),
            Num(lat), Num(lon), Num(speed), status
        }))!;
    
    return ((long)reply[0] == 1, (string?)reply[1] ?? "");
}

// Smoke test
var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var first = await Apply("berlin", t0,        52.5200, 13.4050, 0, "Idle");
var stale = await Apply("berlin", t0 - 1000, 1.0,     1.0,     0, "Moving");

Console.WriteLine($"First sample applied: {first.Applied}");    // Treba true
Console.WriteLine($"Stale sample applied: {stale.Applied}");    // Treba false
Console.WriteLine($"stored lat: {await db.HashGetAsync(Key("truck:berlin"), "lat")}"); 
return 0;