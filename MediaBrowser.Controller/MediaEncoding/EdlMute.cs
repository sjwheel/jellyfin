using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Server-side muting from a sidecar EDL ("{video}.edl", lines "start end 1").
/// Only clients listed in JELLYFIN_EDL_CLIENTS (comma-separated, matched
/// case-insensitively as a substring of the client app name or the device name)
/// get it: for them PlaybackInfo turns off direct play and audio stream copy, and
/// the audio transcode gets a volume=0 filter over each window. The video is still
/// stream copied. They are also the only clients that get the media segments of an
/// item with a sidecar EDL (EdlToMediaSegments plugin skips). Everything else (Kodi,
/// browsers, ...) plays untouched.
/// </summary>
public static class EdlMute
{
    private static readonly string[] _clients = (Environment.GetEnvironmentVariable("JELLYFIN_EDL_CLIENTS") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static readonly ConcurrentDictionary<string, (DateTime Modified, IReadOnlyList<EdlMuteWindow> Windows)> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether EDL muting is enabled for a client.
    /// </summary>
    /// <param name="client">The client app name, e.g. "Jellyfin Android TV".</param>
    /// <param name="device">The device name, e.g. "Basement Google TV streamer".</param>
    /// <returns><c>true</c> if the client or device matches JELLYFIN_EDL_CLIENTS.</returns>
    public static bool AppliesToClient(string? client, string? device)
        => _clients.Any(c => (client?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)
            || (device?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Whether a media file has a sidecar EDL.
    /// </summary>
    /// <param name="mediaPath">The media file path.</param>
    /// <returns><c>true</c> if "{video}.edl" exists.</returns>
    public static bool HasEdl(string? mediaPath)
        => !string.IsNullOrEmpty(mediaPath) && File.Exists(Path.ChangeExtension(mediaPath, ".edl"));

    /// <summary>
    /// Gets the mute windows (EDL action 1) for a media file, or an empty list.
    /// </summary>
    /// <param name="mediaPath">The media file path.</param>
    /// <returns>The mute windows, sorted by start.</returns>
    public static IReadOnlyList<EdlMuteWindow> GetMuteWindows(string? mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            return [];
        }

        var edlPath = Path.ChangeExtension(mediaPath, ".edl");
        try
        {
            var info = new FileInfo(edlPath);
            if (!info.Exists)
            {
                return [];
            }

            if (_cache.TryGetValue(edlPath, out var cached) && cached.Modified == info.LastWriteTimeUtc)
            {
                return cached.Windows;
            }

            var windows = Parse(File.ReadLines(edlPath));
            _cache[edlPath] = (info.LastWriteTimeUtc, windows);
            return windows;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Parses the mute lines of an EDL. Fields may be separated by any whitespace; other actions are ignored.
    /// </summary>
    /// <param name="lines">The EDL lines.</param>
    /// <returns>The mute windows, sorted by start.</returns>
    public static IReadOnlyList<EdlMuteWindow> Parse(IEnumerable<string> lines)
    {
        var windows = new List<EdlMuteWindow>();
        foreach (var line in lines)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && parts[2] == "1"
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && end > start)
            {
                windows.Add(new EdlMuteWindow(start, end));
            }
        }

        windows.Sort((a, b) => a.Start.CompareTo(b.Start));
        return windows;
    }

    /// <summary>
    /// Builds the ffmpeg audio filter that silences the windows.
    /// </summary>
    /// <param name="windows">The mute windows in source seconds.</param>
    /// <param name="offsetSeconds">Seconds to subtract when the output timeline does not keep source timestamps.</param>
    /// <returns>A volume filter, e.g. <c>volume=enable='between(t,1,2)+between(t,5,6)':volume=0</c>.</returns>
    public static string BuildFilter(IReadOnlyList<EdlMuteWindow> windows, double offsetSeconds)
        => "volume=enable='"
            + string.Join('+', windows.Select(w => string.Format(CultureInfo.InvariantCulture, "between(t,{0:0.###},{1:0.###})", w.Start - offsetSeconds, w.End - offsetSeconds)))
            + "':volume=0";
}
