using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using Nocturne.Core.Models.Serializers;
using Xunit;

namespace Nocturne.Core.Models.Tests.Serializers;

public class DocumentCreatedAtWireTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { UploaderIdJsonModifier.RemoveIgnoredBaseProperties },
        },
    };

    private const string UploadedValue = "1999-01-01T00:00:00.000Z";

    public static TheoryData<Type> DocumentTypes()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ProcessableDocumentBase).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ProcessableDocumentBase).IsAssignableFrom(t)))
        {
            data.Add(type);
        }
        return data;
    }

    public static TheoryData<Type> DocumentTypesWithExtensionData()
    {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(ProcessableDocumentBase).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(ProcessableDocumentBase).IsAssignableFrom(t)
                         && t.GetProperties().Any(p => p.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)))
        {
            data.Add(type);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentTypes))]
    public void Serialises_created_at_and_no_camel_case_created_at(Type type)
    {
        var document = (ProcessableDocumentBase)Activator.CreateInstance(type)!;
        document.CreatedAt = UploadedValue;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(document, type, Options));

        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        names.Should().Contain("created_at");
        names.Should().NotContain("createdAt");
        json.RootElement.GetProperty("created_at").GetString().Should().Be(UploadedValue);
    }

    [Theory]
    [MemberData(nameof(DocumentTypesWithExtensionData))]
    public void Uploaded_created_at_extra_round_trips(Type type)
    {
        var read = JsonSerializer.Deserialize(
            $$"""{"created_at":"2020-02-02T00:00:00.000Z","createdAt":"{{UploadedValue}}"}""", type, Options)!;

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(read, type, Options));

        json.RootElement.GetProperty("createdAt").GetString().Should().Be(UploadedValue);
        json.RootElement.GetProperty("created_at").GetString().Should().Be("2020-02-02T00:00:00.000Z");
    }
}
