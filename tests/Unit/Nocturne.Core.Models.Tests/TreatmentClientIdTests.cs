using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary><see cref="TreatmentClientId"/> over the metadata of a state span or profile activation.</summary>
[Trait("Category", "Unit")]
public class TreatmentClientIdTests
{
    private const string ClientId = "0b6f7e4a-1c2d-4e3f-8a9b-0c1d2e3f4a5b";

    private static Treatment Uploaded(object? id)
    {
        var treatment = new Treatment();
        if (id is not null)
            treatment.AdditionalProperties = new() { ["id"] = id };
        return treatment;
    }

    private static Dictionary<string, object> Kept(Treatment treatment)
    {
        var metadata = new Dictionary<string, object>();
        TreatmentClientId.KeepInMetadata(metadata, treatment);
        return metadata;
    }

    private static Dictionary<string, object> AsStored(Dictionary<string, object> metadata) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(metadata))!;

    private static Treatment Served(IDictionary<string, object>? metadata)
    {
        var treatment = new Treatment();
        TreatmentClientId.RestoreFromMetadata(treatment, metadata);
        return treatment;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_client_id_is_served_from_a_span_in_memory_or_read_back(bool readBack)
    {
        var metadata = Kept(Uploaded(ClientId));

        var served = Served(readBack ? AsStored(metadata) : metadata);

        TreatmentClientId.Of(served).Should().Be(ClientId);
    }

    [Fact]
    public void A_client_id_sent_as_a_json_string_is_kept()
    {
        var served = Served(Kept(Uploaded(JsonSerializer.SerializeToElement(ClientId))));

        TreatmentClientId.Of(served).Should().Be(ClientId);
    }

    [Fact]
    public void A_span_keeps_no_client_id_when_none_or_json_null_was_sent()
    {
        Kept(Uploaded(null)).Should().BeEmpty();
        Kept(Uploaded(JsonSerializer.SerializeToElement<string?>(null))).Should().BeEmpty();
    }

    [Fact]
    public void A_span_with_no_client_id_or_no_metadata_serves_none()
    {
        TreatmentClientId.Of(Served(new Dictionary<string, object>())).Should().BeNull();
        TreatmentClientId.Of(Served(null)).Should().BeNull();
        Served(AsStored(new Dictionary<string, object> { ["id"] = JsonSerializer.SerializeToElement<string?>(null)! }))
            .AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public void Serving_a_client_id_leaves_the_other_additional_properties()
    {
        var served = new Treatment { AdditionalProperties = new() { ["device"] = "Pixel" } };

        TreatmentClientId.RestoreFromMetadata(served, new Dictionary<string, object> { ["id"] = ClientId });

        served.AdditionalProperties.Should().Contain("device", "Pixel").And.Contain("id", ClientId);
    }
}
