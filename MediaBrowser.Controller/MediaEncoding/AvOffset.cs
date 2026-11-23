using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Per-video-format audio delay (lip-sync pre-correction), read from the JSON file named by JELLYFIN_AV_OFFSET_CONFIG:
/// <code>
/// {"clients": ["Google TV streamer"],
///  "rules": [{"res": "2160p", "range": "dv", "fps": "23.976", "audio_delay_ms": -20},
///            {"res": "1080p", "audio_delay_ms": 10}]}
/// </code>
/// <c>clients</c> are matched case-insensitively as a substring of the client app name or device name.
/// A rule matches when each of its keys (<c>res</c>: sd/720p/1080p/2160p, <c>range</c>: sdr/hdr10/hlg/dv,
/// <c>fps</c>: 23.976/24/25/29.97/30/50/59.94/60) is absent, "*" or equal to the video's class; the rule with the
/// most keys wins (first one on a tie). <c>audio_delay_ms</c> &gt; 0 plays the audio later, &lt; 0 earlier.
/// For a matching client with a non-zero delay, PlaybackInfo turns off direct play/stream and audio copy; the audio
/// is transcoded with its timestamps shifted while the video is still copied. The file is re-read when it changes
/// (a ConfigMap directory mount, not subPath, updates live).
/// </summary>
public static class AvOffset
{
    private static readonly (string Name, double Fps)[] _fpsClasses =
    [
        ("23.976", 24000.0 / 1001), ("24", 24), ("25", 25), ("29.97", 30000.0 / 1001), ("30", 30),
        ("50", 50), ("59.94", 60000.0 / 1001), ("60", 60)
    ];

    private static readonly object _lock = new();
    private static string? _loadedPath;
    private static DateTime _loadedMtime;
    private static DateTime _lastCheck;
    private static Config _config = Config.Empty;

    /// <summary>
    /// Gets the audio delay in milliseconds for a client and video stream (0 = no correction).
    /// </summary>
    /// <param name="client">The client app name.</param>
    /// <param name="device">The device name.</param>
    /// <param name="video">The source video stream.</param>
    /// <returns>The audio delay in ms.</returns>
    public static double GetDelayMs(string? client, string? device, MediaStream? video)
        => video is null ? 0 : GetDelayMs(Load(Environment.GetEnvironmentVariable("JELLYFIN_AV_OFFSET_CONFIG")), client, device, video);

    /// <summary>
    /// Gets the audio delay for a parsed config.
    /// </summary>
    /// <param name="config">The config.</param>
    /// <param name="client">The client app name.</param>
    /// <param name="device">The device name.</param>
    /// <param name="video">The source video stream.</param>
    /// <returns>The audio delay in ms.</returns>
    public static double GetDelayMs(Config config, string? client, string? device, MediaStream video)
        => GetDelayMs(config, client, device, video.Width, video.Height, video.VideoRangeType, video.RealFrameRate ?? video.AverageFrameRate);

    /// <summary>
    /// Gets the audio delay for a parsed config and video properties.
    /// </summary>
    /// <param name="config">The config.</param>
    /// <param name="client">The client app name.</param>
    /// <param name="device">The device name.</param>
    /// <param name="width">The video width.</param>
    /// <param name="height">The video height.</param>
    /// <param name="rangeType">The video range type.</param>
    /// <param name="frameRate">The frame rate.</param>
    /// <returns>The audio delay in ms.</returns>
    public static double GetDelayMs(Config config, string? client, string? device, int? width, int? height, VideoRangeType rangeType, float? frameRate)
    {
        if (!config.Clients.Any(c => (client?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)
            || (device?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            return 0;
        }

        var res = ResolutionClass(width, height);
        var range = RangeClass(rangeType);
        var fps = FpsClass(frameRate);
        Rule? best = null;
        var bestScore = -1;
        foreach (var rule in config.Rules)
        {
            if (!Matches(rule.Res, res) || !Matches(rule.Range, range) || !Matches(rule.Fps, fps))
            {
                continue;
            }

            var score = (IsSet(rule.Res) ? 1 : 0) + (IsSet(rule.Range) ? 1 : 0) + (IsSet(rule.Fps) ? 1 : 0);
            if (score > bestScore)
            {
                best = rule;
                bestScore = score;
            }
        }

        return best?.AudioDelayMs ?? 0;
    }

    /// <summary>
    /// Builds the ffmpeg audio filter that shifts the audio timestamps.
    /// </summary>
    /// <param name="delayMs">The audio delay in ms.</param>
    /// <returns>The filter.</returns>
    public static string BuildFilter(double delayMs)
        => string.Format(CultureInfo.InvariantCulture, "asetpts=PTS{0:+0.000;-0.000}/TB", delayMs / 1000.0);

    /// <summary>
    /// Gets the resolution class: sd, 720p, 1080p or 2160p.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <returns>The class.</returns>
    public static string ResolutionClass(int? width, int? height)
    {
        int w = width ?? 0, h = height ?? 0;
        if (w > 2600 || h > 1600)
        {
            return "2160p";
        }

        if (w > 1400 || h > 900)
        {
            return "1080p";
        }

        return w > 1000 || h > 600 ? "720p" : "sd";
    }

    /// <summary>
    /// Gets the dynamic range class: sdr, hdr10 (incl. HDR10+), hlg or dv.
    /// </summary>
    /// <param name="type">The video range type.</param>
    /// <returns>The class.</returns>
    public static string RangeClass(VideoRangeType type) => type switch
    {
        VideoRangeType.HDR10 or VideoRangeType.HDR10Plus => "hdr10",
        VideoRangeType.HLG => "hlg",
        VideoRangeType.DOVI or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHLG or VideoRangeType.DOVIWithSDR
            or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithHDR10Plus or VideoRangeType.DOVIWithELHDR10Plus => "dv",
        _ => "sdr"
    };

    /// <summary>
    /// Gets the frame rate class (nearest of 23.976/24/25/29.97/30/50/59.94/60), or "other".
    /// </summary>
    /// <param name="fps">The frame rate.</param>
    /// <returns>The class.</returns>
    public static string FpsClass(float? fps)
    {
        if (fps is not > 0)
        {
            return "other";
        }

        var (name, value) = _fpsClasses.MinBy(c => Math.Abs(c.Fps - fps.Value));
        return Math.Abs(value - fps.Value) < 0.01 ? name : "other";
    }

    /// <summary>
    /// Parses a config document.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The config.</returns>
    public static Config Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var clients = new List<string>();
        if (root.TryGetProperty("clients", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            clients.AddRange(c.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0));
        }

        var rules = new List<Rule>();
        if (root.TryGetProperty("rules", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in r.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("audio_delay_ms", out var d) || d.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                rules.Add(new Rule(Str(e, "res"), Str(e, "range"), Str(e, "fps"), d.GetDouble()));
            }
        }

        return new Config(clients, rules);
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    private static bool IsSet(string? key) => !string.IsNullOrEmpty(key) && key != "*";

    private static bool Matches(string? key, string value) => !IsSet(key) || string.Equals(key, value, StringComparison.OrdinalIgnoreCase);

    private static Config Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Config.Empty;
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (path == _loadedPath && now - _lastCheck < TimeSpan.FromSeconds(10))
            {
                return _config;
            }

            _lastCheck = now;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (path != _loadedPath || mtime != _loadedMtime)
                {
                    _config = File.Exists(path) ? Parse(File.ReadAllText(path)) : Config.Empty;
                    _loadedPath = path;
                    _loadedMtime = mtime;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Keep the last good config.
            }

            return _config;
        }
    }

    /// <summary>
    /// A parsed config.
    /// </summary>
    /// <param name="Clients">Client/device substrings.</param>
    /// <param name="Rules">Delay rules.</param>
    public sealed record Config(IReadOnlyList<string> Clients, IReadOnlyList<Rule> Rules)
    {
        /// <summary>
        /// Gets the empty config.
        /// </summary>
        public static Config Empty { get; } = new([], []);
    }

    /// <summary>
    /// One delay rule.
    /// </summary>
    /// <param name="Res">Resolution class or null/"*".</param>
    /// <param name="Range">Dynamic range class or null/"*".</param>
    /// <param name="Fps">Frame rate class or null/"*".</param>
    /// <param name="AudioDelayMs">Audio delay in ms (&gt; 0 = later).</param>
    public sealed record Rule(string? Res, string? Range, string? Fps, double AudioDelayMs);
}
