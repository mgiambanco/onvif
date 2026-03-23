using SharpOnvifClient;
using OvifViewer.Models;
using Serilog;

namespace OvifViewer.Services;

/// <summary>
/// Discovers ONVIF cameras using SharpOnvifClient's built-in OnvifDiscoveryClient
/// which handles WS-Discovery multicast (UDP 3702) correctly.
/// </summary>
public class WsDiscoveryService : IDiscoveryService
{
    public async Task<IReadOnlyList<DiscoveredCamera>> DiscoverAsync(
        int timeoutMs = 3000,
        IProgress<DiscoveredCamera>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, DiscoveredCamera>(StringComparer.OrdinalIgnoreCase);

        Log.Information("WS-Discovery: starting scan (timeout={Timeout}ms)", timeoutMs);

        void OnDeviceFound(OnvifDiscoveryResult result)
        {
            // Addresses is a space-separated list of XAddrs; take the first HTTP one
            var deviceUrl = result.Addresses
                .FirstOrDefault(a => a.StartsWith("http", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(deviceUrl))
            {
                Log.Debug("WS-Discovery: ignoring result with no HTTP XAddr — {Raw}", result.Raw);
                return;
            }

            var cam = new DiscoveredCamera
            {
                DeviceServiceUrl = deviceUrl,
                DisplayName = result.Name ?? result.Hardware ?? deviceUrl,
                Manufacturer = result.Manufacturer ?? string.Empty,
                Scopes = result.Scopes?.ToList() ?? [],
            };

            lock (results)
            {
                if (results.ContainsKey(deviceUrl)) return;
                results[deviceUrl] = cam;
            }

            Log.Information("WS-Discovery: found {Url}", deviceUrl);
            progress?.Report(cam);
        }

        try
        {
            await OnvifDiscoveryClient.DiscoverAsync(OnDeviceFound, timeoutMs)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* expected when scan ends */ }
        catch (Exception ex)
        {
            Log.Error(ex, "WS-Discovery scan encountered an error");
        }

        Log.Information("WS-Discovery: scan complete, {Count} device(s) found", results.Count);
        return results.Values.ToList();
    }
}
