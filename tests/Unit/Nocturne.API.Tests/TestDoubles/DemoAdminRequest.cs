using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Nocturne.Core.Contracts.Multitenancy;
using Nocturne.Core.Contracts.V4;

namespace Nocturne.API.Tests.TestDoubles;

/// <summary>
/// The request a platform admin makes to the demo admin routes: its own scope resolves no tenant, and
/// every <see cref="ILegacyReadCacheInvalidation"/> report made from a scope opened beside it is
/// recorded with the tenant that scope read as.
/// </summary>
internal sealed class DemoAdminRequest
{
    public DemoAdminRequest()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITenantAccessor, ScopeTenantAccessor>();
        services.AddScoped(_ => Mock.Of<ICategoryReadContext>());
        services.AddScoped<ILegacyReadCacheInvalidation>(
            sp => new TenantRecordingLegacyReads(sp.GetRequiredService<ITenantAccessor>(), Reports));

        Context = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                RequestServices = services.BuildServiceProvider().CreateScope().ServiceProvider,
            },
        };
    }

    public ControllerContext Context { get; }

    public List<(Guid? Tenant, IReadOnlyCollection<Type> Models)> Reports { get; } = [];

    private sealed class ScopeTenantAccessor : ITenantAccessor
    {
        public Guid TenantId => Context?.TenantId ?? Guid.Empty;

        public bool IsResolved => Context is not null;

        public TenantContext? Context { get; private set; }

        public void SetTenant(TenantContext context) => Context = context;
    }

    private sealed class TenantRecordingLegacyReads(
        ITenantAccessor tenant, List<(Guid? Tenant, IReadOnlyCollection<Type> Models)> reports)
        : ILegacyReadCacheInvalidation
    {
        public Task WrittenAsync(IReadOnlyCollection<Type> models)
        {
            reports.Add((tenant.Context?.TenantId, models));
            return Task.CompletedTask;
        }
    }
}
