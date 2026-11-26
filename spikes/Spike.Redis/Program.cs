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
var server = mux.GetServer(mux.GetEndPoints()[0]);
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

// Test Helpers
var failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "Pass" : "FAIL")}    {name}    {detail}");
    if(!ok) failures++;
}

async Task Cleanup()
{
    foreach (var key in server.Keys(pattern: Key("*")).ToArray())
        await db.KeyDeleteAsync(key);
}

// Functional tests
await Cleanup();

var trucks = new (string Id, double Lat, double Lon, string Status)[]
{
    ("berlin",   52.5200, 13.4050, "Moving"),
    ("prague",   50.0755, 14.4378, "Idle"),
    ("warsaw",   52.2297, 21.0122, "Stopped"),
    ("vienna",   48.2082, 16.3738, "Moving"),
    ("budapest", 47.4979, 19.0402, "Idle"),
};

var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

// Samples for every truck
var allApplied = true;
foreach (var t in trucks)
{
    var r = await Apply(t.Id, t0, t.Lat, t.Lon, t.Status == "Moving" ? 80 : 0, t.Status);
    allApplied &= r.Applied;
}
Check("first sample for 5 trucks applied", allApplied);
// stale/dupes should(have to be) be ignored
var stale = await Apply("berlin", t0 - 1000, 0, 0, 0, "Stopped");
Check("older sample rejected", !stale.Applied);
var duplicates = await Apply("berlin", t0 - 1000, 0, 0, 0, "Stopped");
Check("dupe timestamp rejected", !duplicates.Applied);
var latStored = (double)await db.HashGetAsync(Key("truck:berlin"), "lat");
Check("rejected samples did not overwrite state", Math.Abs(latStored - 52.52) < 1e-9, $"lat={latStored}");
// status transition moves the truck between status sets
var move = await Apply("berlin", t0 + 10_000, 52.5200, 13.4500, 0, "Idle");
Check("Moving -> Idle applied, previous status returned", move.Applied && move.PrevStatus == "Moving", $"prev={move.PrevStatus}");

var moving = await db.SetLengthAsync(Key("status:Moving"));
var idle = await db.SetLengthAsync(Key("status:Idle"));
var stopped = await db.SetLengthAsync(Key("status:Stopped"));
Check("status coutns: Moving=1 Idle=3 Stopped=1", moving == 1 && idle == 3 && stopped == 1, $"Moving={moving} Idle={idle} Stopped={stopped}");

// last-seen index
var score = await db.SortedSetScoreAsync(Key("lastseen"), "berlin");
Check("lastseen holds the newest timestamp", score == t0 + 10_000, $"score={score}");
// geospatial search: trcks within 350km of berlin
var near = await db.GeoSearchAsync(
    Key("geo"), 13.4050, 52.5200,
    new GeoSearchCircle(350, GeoUnit.Kilometers),
    options: GeoRadiusOptions.WithDistance
);

var names = near
        .Select(r => r.Member.ToString())
        .OrderBy(n => n)
        .ToArray();
Check("GEORESERCH 350km around Berlin = berlin + prague", names.SequenceEqual(new[] {"berlin", "prague"}),
        string.Join(", ", near.Select(r => $"{r.Member} = {r.Distance:N0} km")));
Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\nFAILED: {failures}");
return failures == 0 ? 0 : 1;