namespace HuePC.Core.Interfaces;

public interface IDeviceAliasStore
{
    Task<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(IReadOnlyDictionary<string, string> aliases, CancellationToken cancellationToken = default);
}
