using LibVLCSharp.Shared;
using Serilog;

namespace OvifViewer.Services;

public class RtspStreamService : IRtspStreamService
{
    public readonly LibVLC LibVlc;

    public RtspStreamService(LibVLC libVlc)
    {
        LibVlc = libVlc;
    }

    public MediaPlayer CreatePlayer(string rtspUri, string username, string password)
    {
        var player = new MediaPlayer(LibVlc);
        Play(player, rtspUri, username, password);
        return player;
    }

    public void Play(MediaPlayer player, string rtspUri, string username, string password)
    {
        Stop(player);

        var media = BuildMedia(rtspUri, username, password);
        player.Play(media);
        // Media is owned by the player after Play(); we dispose our reference.
        media.Dispose();
    }

    public void Stop(MediaPlayer player)
    {
        if (player.IsPlaying)
            player.Stop();
    }

    public void Destroy(MediaPlayer player)
    {
        try
        {
            Stop(player);
            player.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error destroying media player");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Media BuildMedia(string rtspUri, string username, string password)
    {
        var media = new Media(LibVlc, new Uri(rtspUri));

        // Prefer TCP — more reliable through switches/firewalls
        media.AddOption(":rtsp-tcp");
        media.AddOption(":network-caching=300");

        // Pass credentials via options so they don't appear in the URI/logs
        if (!string.IsNullOrEmpty(username))
        {
            media.AddOption($":rtsp-user={username}");
            media.AddOption($":rtsp-pwd={password}");
        }

        return media;
    }
}
