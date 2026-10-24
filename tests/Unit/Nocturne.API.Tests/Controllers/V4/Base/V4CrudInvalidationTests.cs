using System.Reflection;
using FluentAssertions;
using Nocturne.API.Controllers.V4.Base;
using OpenApi.Remote.Attributes;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V4.Base;

/// <summary>
/// A delete or restore changes both the live list and the deleted list every derived controller
/// exports, so the base's commands must name both reads for the generated command to refresh them.
/// </summary>
[Trait("Category", "Unit")]
public class V4CrudInvalidationTests
{
    [Theory]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Delete), nameof(V4ReadOnlyControllerBase<,>.GetAll))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Delete), nameof(V4CrudControllerBase<,,,>.ListDeleted))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Restore), nameof(V4ReadOnlyControllerBase<,>.GetAll))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.Restore), nameof(V4CrudControllerBase<,,,>.ListDeleted))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.BulkRestore), nameof(V4ReadOnlyControllerBase<,>.GetAll))]
    [InlineData(nameof(V4CrudControllerBase<,,,>.BulkRestore), nameof(V4CrudControllerBase<,,,>.ListDeleted))]
    public void DeletingOrRestoring_RefreshesTheListReads(string write, string read)
    {
        Command(write).Invalidates.Should().Contain(read);
    }

    [Theory]
    [InlineData(typeof(V4ReadOnlyControllerBase<,>), nameof(V4ReadOnlyControllerBase<,>.GetAll))]
    [InlineData(typeof(V4CrudControllerBase<,,,>), nameof(V4CrudControllerBase<,,,>.ListDeleted))]
    public void EveryRefreshedRead_IsAQueryTheCommandCanRefresh(Type owner, string read)
    {
        owner.GetMethod(read)!.GetCustomAttribute<RemoteQueryAttribute>().Should().NotBeNull();
    }

    private static RemoteCommandAttribute Command(string write) =>
        typeof(V4CrudControllerBase<,,,>).GetMethod(write)!.GetCustomAttribute<RemoteCommandAttribute>()!;
}
