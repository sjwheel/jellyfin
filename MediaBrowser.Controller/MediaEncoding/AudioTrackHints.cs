using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Per-file audio hints for single audio track mode, read from the JSON file named by JELLYFIN_AUDIO_TRACK_HINTS:
/// <c>{"files": {"/media/movies/x/x.mkv": {"orig": "jpn", "skip": {"5": "commentary", "3": "same-mix"}}}}</c>.
/// <c>orig</c> is the film's original language (ISO 639-2); <c>skip</c> lists audio streams (by index) that must not be
/// offered (commentaries, or the same mix as another offered track). The file is re-read when it changes.
/// Files without hints fall back to the language of the default track and title-based commentary detection.
/// </summary>
public static class AudioTrackHints
{
    private static readonly object _lock = new();
    private static string? _loadedPath;
    private static DateTime _loadedMtime;
    private static DateTime _lastCheck;
    private static Dictionary<string, FileHints> _files = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the hints for a media file, or <c>null</c>.
    /// </summary>
    /// <param name="mediaPath">The media file path as Jellyfin sees it.</param>
    /// <returns>The hints.</returns>
    public static FileHints? Get(string? mediaPath)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            return null;
        }

        var files = Load(Environment.GetEnvironmentVariable("JELLYFIN_AUDIO_TRACK_HINTS"));
        return files.TryGetValue(mediaPath, out var hints) ? hints : null;
    }

    /// <summary>
    /// Parses a hints document.
    /// </summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The hints by media path.</returns>
    public static Dictionary<string, FileHints> Parse(string json)
    {
        var result = new Dictionary<string, FileHints>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var file in files.EnumerateObject())
        {
            string? orig = null;
            var skip = new HashSet<int>();
            if (file.Value.TryGetProperty("orig", out var o) && o.ValueKind == JsonValueKind.String)
            {
                orig = o.GetString();
            }

            if (file.Value.TryGetProperty("skip", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in s.EnumerateObject())
                {
                    if (int.TryParse(entry.Name, out var index))
                    {
                        skip.Add(index);
                    }
                }
            }

            result[file.Name] = new FileHints(orig, skip);
        }

        return result;
    }

    private static Dictionary<string, FileHints> Load(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new Dictionary<string, FileHints>();
        }

        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (path == _loadedPath && now - _lastCheck < TimeSpan.FromSeconds(30))
            {
                return _files;
            }

            _lastCheck = now;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (path != _loadedPath || mtime != _loadedMtime)
                {
                    _files = Parse(File.ReadAllText(path));
                    _loadedPath = path;
                    _loadedMtime = mtime;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Keep the last good hints (or none).
                _loadedPath = path;
            }

            return _files;
        }
    }

    /// <summary>
    /// Hints for one media file.
    /// </summary>
    /// <param name="OriginalLanguage">The original language (ISO 639-2), or <c>null</c>.</param>
    /// <param name="Skip">Audio stream indexes that are not offered.</param>
    public sealed record FileHints(string? OriginalLanguage, IReadOnlySet<int> Skip);
}
