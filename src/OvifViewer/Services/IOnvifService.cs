using OvifViewer.Models;

namespace OvifViewer.Services;

public interface IOnvifService
{
    /// <summary>Fetch device info and populate manufacturer/model on the camera.</summary>
    Task<DiscoveredCamera> GetDeviceInfoAsync(string deviceServiceUrl, string username, string password);

    /// <summary>Returns available media profiles for the given camera.</summary>
    Task<IReadOnlyList<CameraProfile>> GetProfilesAsync(CameraConfig camera);

    /// <summary>Returns the RTSP stream URI for the selected profile.</summary>
    Task<string> GetStreamUriAsync(CameraConfig camera);

    /// <summary>Returns true if the camera has PTZ capabilities.</summary>
    Task<bool> HasPtzAsync(CameraConfig camera);

    /// <summary>Sends a PTZ continuous-move command. Pass 0 to stop an axis.</summary>
    Task PtzMoveAsync(CameraConfig camera, float panSpeed, float tiltSpeed, float zoomSpeed);

    /// <summary>Stops all PTZ movement.</summary>
    Task PtzStopAsync(CameraConfig camera);
}
