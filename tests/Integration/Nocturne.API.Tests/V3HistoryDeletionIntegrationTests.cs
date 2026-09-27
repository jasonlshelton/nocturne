using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nocturne.API.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Nightscout's v3 DELETE keeps the document with <c>isValid: false</c> and a new <c>srvModified</c>,
/// and <c>history</c> returns it (<c>lib/api3/generic/delete</c>, <c>lib/api3/generic/history</c>);
/// a client that syncs through history learns of a delete no other way. Each test creates through v3,
/// syncs, deletes, and reads the history from the cursor the sync left the client at.
/// </summary>
[Trait("Category", "Integration")]
public partial class V3HistoryDeletionIntegrationTests : ApiIntegrationTestBase
{
    private static readonly long Date =
        DateTimeOffset.UtcNow.AddMinutes(-15).ToUnixTimeMilliseconds() / 1000 * 1000;

    private static readonly string Iso =
        DateTimeOffset.FromUnixTimeMilliseconds(Date).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public V3HistoryDeletionIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    [Fact]
    public async Task DeletedEntry_IsServedWithIsValidFalse()
    {
        var id = ObjectId();
        await CreateAsync("entries", new
        {
            _id = id, type = "sgv", sgv = 187, date = Date, device = "it-v3", app = "it", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("entries", doc => IdOf(doc) == id);

        tombstone.GetProperty("sgv").GetInt32().Should().Be(187);
    }

    [Fact]
    public async Task DeletedTreatment_IsServedWithIsValidFalse()
    {
        await CreateAsync("treatments", new
        {
            eventType = "Correction Bolus", insulin = 1.35, date = Date, app = "it", device = "it-v3", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("treatments", doc => Number(doc, "insulin") == 1.35);

        tombstone.GetProperty("eventType").GetString().Should().Be("Correction Bolus");
    }

    [Fact]
    public async Task DeletedDeviceStatus_IsServedWithIsValidFalse()
    {
        var id = ObjectId();
        await CreateAsync("devicestatus", new
        {
            _id = id, date = Date, created_at = Iso, device = "openaps://it-v3", app = "it", utcOffset = 0,
            openaps = new
            {
                iob = new { iob = 0.4, time = Iso },
                suggested = new { bg = 125, eventualBG = 118, reason = "it", timestamp = Iso },
            },
        });

        var tombstone = await DeleteAfterSyncAsync("devicestatus", doc => IdOf(doc) == id);

        tombstone.GetProperty("device").GetString().Should().Be("openaps://it-v3");
    }

    [Fact]
    public async Task DeletedFood_IsServedWithIsValidFalse()
    {
        var id = ObjectId();
        await CreateAsync("food", new
        {
            _id = id, type = "food", name = "it-v3 apple", carbs = 14, portion = 100, unit = "g", date = Date, created_at = Iso,
        });

        var tombstone = await DeleteAfterSyncAsync("food", doc => IdOf(doc) == id);

        tombstone.GetProperty("name").GetString().Should().Be("it-v3 apple");
    }

    [Fact]
    public async Task DeletedProfile_IsServedWithIsValidFalse()
    {
        var id = ObjectId();
        ScheduleEntry[] schedule = [new("00:00", 1.0, 0)];
        await CreateAsync("profile", new
        {
            _id = id,
            defaultProfile = "Default",
            startDate = Iso,
            created_at = Iso,
            mills = Date,
            units = "mg/dl",
            store = new Dictionary<string, object>
            {
                ["Default"] = new
                {
                    dia = 4, units = "mg/dl", timezone = "UTC",
                    basal = schedule, carbratio = schedule, sens = schedule,
                    target_low = schedule, target_high = schedule,
                },
            },
        });

        var tombstone = await DeleteAfterSyncAsync("profile", doc => IdOf(doc) == id);

        tombstone.GetProperty("defaultProfile").GetString().Should().Be("Default");
    }

    [Fact]
    public async Task Delete_MovesTheCollectionLastModified_ToTheTombstone()
    {
        await CreateAsync("treatments", new
        {
            eventType = "Correction Bolus", insulin = 2.15, date = Date, app = "it", device = "it-v3", utcOffset = 0,
        });

        var tombstone = await DeleteAfterSyncAsync("treatments", doc => Number(doc, "insulin") == 2.15);

        var lastModified = await AuthenticatedClient.GetFromJsonAsync<JsonElement>("/api/v3/lastModified");
        lastModified.GetProperty("result").GetProperty("collections").GetProperty("treatments").GetInt64()
            .Should().BeGreaterThanOrEqualTo(tombstone.GetProperty("srvModified").GetInt64());
    }

    /// <summary>
    /// Syncs <paramref name="collection"/> from the start, deletes the document
    /// <paramref name="match"/> finds by the identifier history served, and returns that document as
    /// the history read from the sync's cursor serves it.
    /// </summary>
    private async Task<JsonElement> DeleteAfterSyncAsync(string collection, Func<JsonElement, bool> match)
    {
        var (synced, cursor) = await HistoryAsync(collection, 0);
        var live = synced.Should().ContainSingle(doc => match(doc)).Subject;
        (live.TryGetProperty("isValid", out var valid) && valid.ValueKind == JsonValueKind.False)
            .Should().BeFalse("the document is live");

        var deleted = await AuthenticatedClient.DeleteAsync($"/api/v3/{collection}/{IdOf(live)}");
        deleted.IsSuccessStatusCode.Should().BeTrue();

        var (next, nextCursor) = await HistoryAsync(collection, cursor);
        var tombstone = next.Should().ContainSingle(doc => IdOf(doc) == IdOf(live)).Subject;
        tombstone.GetProperty("isValid").GetBoolean().Should().BeFalse();
        nextCursor.Should().BeGreaterThan(cursor);
        // Nocturne's food documents carry no srvModified, deleted or not; the cursor above covers them.
        if (tombstone.TryGetProperty("srvModified", out var srvModified))
        {
            srvModified.GetInt64().Should().BeGreaterThan(cursor);
            nextCursor.Should().BeGreaterThanOrEqualTo(srvModified.GetInt64());
        }

        var (drained, _) = await HistoryAsync(collection, nextCursor);
        drained.Should().NotContain(doc => IdOf(doc) == IdOf(live));
        return tombstone;
    }

    private async Task<(List<JsonElement> Docs, long Cursor)> HistoryAsync(string collection, long since)
    {
        var response = await AuthenticatedClient.GetAsync($"/api/v3/{collection}/history/{since}?limit=100");
        response.IsSuccessStatusCode.Should().BeTrue();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var docs = body.GetProperty("result").EnumerateArray().ToList();
        if (response.Headers.ETag is not { } etag)
        {
            docs.Should().BeEmpty("a page with documents carries its cursor");
            return (docs, since);
        }

        return (docs, long.Parse(CursorPattern().Match(etag.ToString()).Groups[1].Value));
    }

    private async Task CreateAsync(string collection, object document)
    {
        var response = await AuthenticatedClient.PostAsJsonAsync($"/api/v3/{collection}", document);
        response.IsSuccessStatusCode.Should().BeTrue(await response.Content.ReadAsStringAsync());
    }

    private static string? IdOf(JsonElement doc) =>
        doc.TryGetProperty("identifier", out var identifier) ? identifier.GetString()
        : doc.TryGetProperty("_id", out var id) ? id.GetString()
        : null;

    private static double? Number(JsonElement doc, string property) =>
        doc.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static string ObjectId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    [GeneratedRegex("\"(\\d+)\"")]
    private static partial Regex CursorPattern();

    private sealed record ScheduleEntry(string time, double value, int timeAsSeconds);
}
