using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.API.Tests.Integration.Infrastructure;
using Nocturne.Core.Constants;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Models;
using Nocturne.Infrastructure.Data;
using Xunit;
using Xunit.Abstractions;

namespace Nocturne.API.Tests.Integration;

/// <summary>
/// Temporary Override, Temporary Target and Profile Switch treatments are stored only as state spans.
/// LoopFollow, LoopCaregiver and AAPS followers read them back through the v1 and v3 treatment reads,
/// and Loop deletes them by the id it uploaded them under.
/// </summary>
[Trait("Category", "Integration")]
public class StateSpanTreatmentReadsIntegrationTests : ApiIntegrationTestBase
{
    public StateSpanTreatmentReadsIntegrationTests(ApiIntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    private static string MinutesAgo(int minutes) =>
        DateTime.UtcNow.AddMinutes(-minutes).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    public static TheoryData<string> EventTypes =>
        new() { "Temporary Override", "Temporary Target", "Profile Switch" };

    private static object Upload(string eventType, int minutesAgo = 30) => eventType switch
    {
        "Temporary Override" => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "Loop",
            reason = "Running",
            duration = 60,
            insulinNeedsScaleFactor = 0.8,
            targetTop = 140,
            targetBottom = 120,
        },
        "Temporary Target" => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "AndroidAPS",
            reason = "Activity",
            duration = 45,
            targetTop = 8.0,
            targetBottom = 8.0,
            units = "mmol",
        },
        _ => new
        {
            eventType,
            created_at = MinutesAgo(minutesAgo),
            enteredBy = "AndroidAPS",
            profile = "Weekend",
            duration = 0,
        },
    };

    private async Task PostAsync(HttpClient client, object treatment)
    {
        var response = await client.PostAsJsonAsync("/api/v1/treatments", new[] { treatment });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonArray> GetArrayAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
    }

    private static async Task<JsonArray> GetV3ResultAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!.AsArray();
    }

    private static async Task<long> CountAsync(HttpClient client, string query = "")
    {
        var rows = await GetArrayAsync(client, $"/api/v1/count/treatments/where{query}");
        return rows.Count == 0 ? 0 : rows[0]!["count"]!.GetValue<long>();
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_is_served_by_the_v1_list_find_and_count(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));

        var all = await GetArrayAsync(client, "/api/v1/treatments");
        all.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);

        var found = await GetArrayAsync(
            client, $"/api/v1/treatments?find[eventType]={Uri.EscapeDataString(eventType)}");
        found.Should().ContainSingle();

        (await CountAsync(client)).Should().Be(1);
        (await CountAsync(client, $"?find[eventType]={Uri.EscapeDataString(eventType)}")).Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task A_state_span_treatment_is_served_by_v3_search_and_history(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));

        var search = await GetV3ResultAsync(client, "/api/v3/treatments");
        search.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);

        var history = await GetV3ResultAsync(client, "/api/v3/treatments/history/0");
        history.Should().ContainSingle(t => t!["eventType"]!.GetValue<string>() == eventType);
    }

    [Theory]
    [MemberData(nameof(EventTypes))]
    public async Task Deleting_by_the_served_id_removes_the_state_span(string eventType)
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload(eventType));
        var id = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        (await client.GetAsync($"/api/v1/treatments/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
        (await CountAsync(client)).Should().Be(0);
    }

    [Fact]
    public async Task Updating_by_the_served_id_rewrites_the_state_span_in_place()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Override"));
        var id = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!["_id"]!.GetValue<string>();

        var put = await client.PutAsJsonAsync($"/api/v1/treatments/{id}", new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(30),
            enteredBy = "Loop",
            reason = "Walking",
            duration = 90,
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Should().ContainSingle().Which!;
        served["_id"]!.GetValue<string>().Should().Be(id);
        served["reason"]!.GetValue<string>().Should().Be("Walking");
        served["duration"]!.GetValue<double>().Should().Be(90);
    }

    [Fact]
    public async Task Loop_deletes_an_override_by_the_uuid_it_uploaded_it_under()
    {
        var client = CreateAuthenticatedClient();
        var syncIdentifier = Guid.NewGuid().ToString().ToUpperInvariant();
        await PostAsync(client, new
        {
            _id = syncIdentifier,
            eventType = "Temporary Override",
            created_at = MinutesAgo(10),
            enteredBy = "Loop",
            reason = "Pre-Meal",
            durationType = "indefinite",
        });

        var delete = await client.DeleteAsync($"/api/v1/treatments/{syncIdentifier}");

        delete.StatusCode.Should().Be(HttpStatusCode.OK);
        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_temp_target_resolves_by_the_object_id_its_uploaded_uuid_is_served_as(bool upperCase)
    {
        var client = CreateAuthenticatedClient();
        var uuid = Guid.NewGuid();
        var uploadedId = upperCase ? uuid.ToString().ToUpperInvariant() : uuid.ToString();
        await PostAsync(client, new
        {
            _id = uploadedId,
            eventType = "Temporary Target",
            created_at = MinutesAgo(10),
            duration = 30,
            targetTop = 7.0,
            targetBottom = 7.0,
        });
        var objectId = uuid.ToString("N")[..24];

        (await client.GetAsync($"/api/v1/treatments/{objectId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var delete = await client.DeleteAsync($"/api/v1/treatments/{objectId}");

        (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(1);
        (await GetArrayAsync(client, "/api/v1/treatments")).Should().BeEmpty();
    }

    [Fact]
    public async Task An_override_is_served_with_the_fields_it_was_uploaded_with()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Override"));

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!;

        served["duration"]!.GetValue<double>().Should().Be(60);
        served["reason"]!.GetValue<string>().Should().Be("Running");
        served["insulinNeedsScaleFactor"]!.GetValue<double>().Should().Be(0.8);
        served["targetTop"]!.GetValue<double>().Should().Be(140);
        served["targetBottom"]!.GetValue<double>().Should().Be(120);
        served["enteredBy"]!.GetValue<string>().Should().Be("Loop");
    }

    [Fact]
    public async Task An_override_ended_by_the_next_one_is_served_with_its_real_duration()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(50),
            reason = "Indefinite",
            durationType = "indefinite",
        });
        await PostAsync(client, new
        {
            eventType = "Temporary Override",
            created_at = MinutesAgo(20),
            reason = "Next",
            duration = 60,
        });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Override");

        served.Single(t => t!["reason"]!.GetValue<string>() == "Indefinite")!["duration"]!
            .GetValue<double>().Should().Be(30);
    }

    [Fact]
    public async Task A_temp_target_cancel_is_served_beside_the_target_with_zero_duration()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 30));
        await PostAsync(client, new
        {
            eventType = "Temporary Target Cancel",
            created_at = MinutesAgo(10),
            enteredBy = "AndroidAPS",
            duration = 0,
        });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Temporary%20Target");

        served.Should().HaveCount(2);
        served.Select(t => t!["duration"]!.GetValue<double>()).Should().BeEquivalentTo(new[] { 45.0, 0.0 });
        served.Single(t => t!["duration"]!.GetValue<double>() == 45)!["units"]!.GetValue<string>()
            .Should().Be("mmol");
    }

    [Fact]
    public async Task A_profile_switch_is_served_with_its_profile_name()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, Upload("Profile Switch"));

        var served = (await GetArrayAsync(client, "/api/v1/treatments")).Single()!;

        served["profile"]!.GetValue<string>().Should().Be("Weekend");
        served["duration"]!.GetValue<double>().Should().Be(0);
    }

    [Fact]
    public async Task A_superseded_permanent_profile_switch_keeps_duration_zero()
    {
        var client = CreateAuthenticatedClient();
        await PostAsync(client, new { eventType = "Profile Switch", created_at = MinutesAgo(50), profile = "Weekday", duration = 0 });
        await PostAsync(client, new { eventType = "Profile Switch", created_at = MinutesAgo(20), profile = "Weekend", duration = 0 });

        var served = await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Profile%20Switch");

        served.Should().HaveCount(2);
        served.Select(t => t!["duration"]!.GetValue<double>()).Should().AllSatisfy(d => d.Should().Be(0));
    }

    [Fact]
    public async Task Connector_profile_spans_are_not_served_or_deleted_as_treatments()
    {
        var client = CreateAuthenticatedClient();
        var start = DateTime.UtcNow.AddMinutes(-40);
        string[] connectorIds =
        [
            $"glooko_active_profile_{Guid.NewGuid()}_{start.Ticks}",
            $"mylife_active_profile_pump_{start.Ticks}",
        ];
        await WithScopeAsync(async sp =>
        {
            var spans = sp.GetRequiredService<IStateSpanService>();
            await spans.UpsertStateSpanAsync(ConnectorProfile(connectorIds[0], DataSources.GlookoConnector, start));
            await spans.UpsertStateSpanAsync(ConnectorProfile(connectorIds[1], DataSources.MyLifeConnector, start.AddMinutes(5)));
            return 0;
        });
        await PostAsync(client, Upload("Temporary Target", minutesAgo: 10));
        await PostAsync(client, Upload("Profile Switch", minutesAgo: 5));

        var list = await GetArrayAsync(client, "/api/v1/treatments");
        list.Select(t => t!["eventType"]!.GetValue<string>()).Should()
            .BeEquivalentTo(new[] { "Temporary Target", "Profile Switch" });
        list.Single(t => t!["eventType"]!.GetValue<string>() == "Profile Switch")!["profile"]!
            .GetValue<string>().Should().Be("Weekend");
        (await GetArrayAsync(client, "/api/v1/treatments?find[eventType]=Profile%20Switch")).Should().ContainSingle();
        (await CountAsync(client)).Should().Be(2);
        (await CountAsync(client, "?find[eventType]=Profile%20Switch")).Should().Be(1);
        (await GetV3ResultAsync(client, "/api/v3/treatments")).Should().HaveCount(2);
        (await GetV3ResultAsync(client, "/api/v3/treatments/history/0")).Should().HaveCount(2);

        foreach (var id in connectorIds)
        {
            (await client.GetAsync($"/api/v1/treatments/{id}")).StatusCode.Should().NotBe(HttpStatusCode.OK);
            var delete = await client.DeleteAsync($"/api/v1/treatments/{id}");
            (await delete.Content.ReadFromJsonAsync<JsonNode>())!["deletedCount"]!.GetValue<long>().Should().Be(0);
        }

        var stored = await WithScopeAsync(async sp => (await sp.GetRequiredService<IStateSpanService>()
            .GetStateSpansAsync(category: StateSpanCategory.Profile, count: 100)).ToList());
        stored.Select(s => s.OriginalId).Should().Contain(connectorIds);
    }

    private static StateSpan ConnectorProfile(string originalId, string source, DateTime start) => new()
    {
        OriginalId = originalId,
        Category = StateSpanCategory.Profile,
        State = ProfileState.Active.ToString(),
        StartTimestamp = start,
        Source = source,
        Metadata = new Dictionary<string, object> { ["profileName"] = "Unknown" },
    };

    private async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantAccessor>().SetTenant(
            new TenantContext(Fixture.TenantId, ApiIntegrationTestFixture.TenantSlug, "Integration", true, false));
        scope.ServiceProvider.GetRequiredService<NocturneDbContext>().TenantId = Fixture.TenantId;
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task A_devicestatus_override_is_not_served_as_a_treatment()
    {
        var client = CreateAuthenticatedClient();
        var at = MinutesAgo(5);
        var response = await client.PostAsJsonAsync("/api/v1/devicestatus", new
        {
            device = "loop://iPhone",
            created_at = at,
            @override = new { active = true, name = "Workout", timestamp = at, duration = 3600, multiplier = 1.2 },
        });
        response.EnsureSuccessStatusCode();
        await PostAsync(client, new { eventType = "Note", created_at = at, notes = "beside the status" });

        (await GetArrayAsync(client, "/api/v1/treatments")).Should()
            .ContainSingle().Which!["eventType"]!.GetValue<string>().Should().Be("Note");
        (await CountAsync(client)).Should().Be(1);
    }
}
