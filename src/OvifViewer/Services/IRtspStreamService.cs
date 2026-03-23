using LibVLCSharp.Shared;

namespace OvifViewer.Services;

public interface IRtspStreamService
{
    /// <summary>Creates a new MediaPlayer bound to the given RTSP URI.</summary>
    MediaPlayer CreatePlayer(string rtspUri, string username, string password);

    /// <summary>Starts (or restarts) the stream on the given player.</summary>
    void Play(MediaPlayer player, string rtspUri, string username, string password);

    /// <summary>Stops the player and releases its current media.</summary>
    void Stop(MediaPlayer player);

    /// <summary>Permanently disposes the player.</summary>
    void Destroy(MediaPlayer player);
}
