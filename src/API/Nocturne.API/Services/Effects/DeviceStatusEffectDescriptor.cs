
namespace Nocturne.API.Services.Effects;

/// <summary>
/// <see cref="ICollectionEffectDescriptor"/> for the <c>devicestatus</c> collection.
/// <see cref="DeviceStatuses.DeviceStatusService"/> decomposes on its write path, not as a side effect.
/// </summary>
/// <seealso cref="ICollectionEffectDescriptor"/>
public class DeviceStatusEffectDescriptor : ICollectionEffectDescriptor
{
    public string CollectionName => "devicestatus";
    public bool DecomposeToV4 => false;
    public bool BroadcastDataUpdateOnCreate => false;
}
