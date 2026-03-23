using OvifViewer.Models;

namespace OvifViewer.Services;

public interface IDiscoveryService
{
    /// <summary>
    /// Broadcasts WS-Discovery probes on all local network interfaces and
    /// returns cameras that respond within <paramref name="timeoutMs"/>.
    /// </summary>
    Task<IReadOnlyList<DiscoveredCamera>> DiscoverAsync(
        int timeoutMs = 3000,
        IProgress<DiscoveredCamera>? progress = null,
        CancellationToken cancellationToken = default);
}
