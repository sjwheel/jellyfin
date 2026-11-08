using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Extensions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// "One audio track" mode for clients that can only bitstream lossy Dolby audio (e.g. the Google TV Streamer).
/// Clients listed in JELLYFIN_SINGLE_AUDIO_CLIENTS (comma-separated, matched case-insensitively as a substring
/// of the client app name or the device name) see exactly one audio track per video: the best track on the disc
/// (the language of the default track, lossless first, then most channels). The server always plays that track,
/// re-encoded to E-AC-3 (Dolby Digital Plus) 5.1 at JELLYFIN_SINGLE_AUDIO_BITRATE (default 1024 kbps), or stream
/// copied when it already is E-AC-3/AC-3. Direct play is off for those clients; the video is still stream copied
/// when the client supports it. Everything else (Kodi, browsers, ...) is untouched.
/// </summary>
public static class SingleAudioTrack
{
    /// <summary>
    /// The default E-AC-3 bitrate in bits per second.
    /// </summary>
    public const int DefaultBitrate = 1024000;

    /// <summary>
    /// The output codecs in order of preference. AC-3 is only ever used as a stream copy.
    /// </summary>
    public static readonly string[] OutputCodecs = ["eac3", "ac3"];

    private static readonly string[] _clients = ParseClients(Environment.GetEnvironmentVariable("JELLYFIN_SINGLE_AUDIO_CLIENTS"));

    private static readonly int _bitrate = ParseBitrate(Environment.GetEnvironmentVariable("JELLYFIN_SINGLE_AUDIO_BITRATE"));

    /// <summary>
    /// Gets the configured E-AC-3 bitrate for 5.1 output in bits per second.
    /// </summary>
    public static int Bitrate => _bitrate;

    /// <summary>
    /// Whether single audio track mode is enabled for a client.
    /// </summary>
    /// <param name="client">The client app name, e.g. "Jellyfin Android TV".</param>
    /// <param name="device">The device name, e.g. "Basement Google TV streamer".</param>
    /// <returns><c>true</c> if the client or device matches JELLYFIN_SINGLE_AUDIO_CLIENTS.</returns>
    public static bool AppliesToClient(string? client, string? device)
        => AppliesToClient(_clients, client, device);

    /// <summary>
    /// Whether a client matches a list of client/device name fragments.
    /// </summary>
    /// <param name="clients">The name fragments.</param>
    /// <param name="client">The client app name.</param>
    /// <param name="device">The device name.</param>
    /// <returns><c>true</c> on a match.</returns>
    public static bool AppliesToClient(IReadOnlyList<string> clients, string? client, string? device)
        => clients.Any(c => (client?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false)
            || (device?.Contains(c, StringComparison.OrdinalIgnoreCase) ?? false));

    /// <summary>
    /// Parses a comma-separated client list.
    /// </summary>
    /// <param name="value">The list.</param>
    /// <returns>The entries.</returns>
    public static string[] ParseClients(string? value)
        => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Parses a bitrate such as "1024000", "1024k" or "1.5M".
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>Bits per second, or <see cref="DefaultBitrate"/> when empty or invalid.</returns>
    public static int ParseBitrate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultBitrate;
        }

        value = value.Trim();
        var multiplier = 1d;
        if (value.EndsWith('k') || value.EndsWith('K'))
        {
            multiplier = 1000;
            value = value[..^1];
        }
        else if (value.EndsWith('m') || value.EndsWith('M'))
        {
            multiplier = 1000000;
            value = value[..^1];
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number > 0)
        {
            // The E-AC-3 encoder accepts up to 6144 kbps; below 192 kbps 5.1 makes no sense.
            return (int)Math.Clamp(number * multiplier, 192000, 6144000);
        }

        return DefaultBitrate;
    }

    /// <summary>
    /// Gets the E-AC-3 bitrate for an output channel count.
    /// </summary>
    /// <param name="outputChannels">The output channels.</param>
    /// <returns>Bits per second: the configured bitrate for 5.1, scaled down for fewer channels.</returns>
    public static int GetOutputBitrate(int? outputChannels)
    {
        var channels = outputChannels ?? 6;
        return channels >= 6 || channels <= 0 ? _bitrate : Math.Min(_bitrate, channels * 192000);
    }

    /// <summary>
    /// Whether a stream is lossless audio.
    /// </summary>
    /// <param name="stream">The audio stream.</param>
    /// <returns><c>true</c> for TrueHD, DTS-HD MA, FLAC, PCM, ...</returns>
    public static bool IsLossless(MediaStream stream)
    {
        var codec = stream.Codec ?? string.Empty;
        if (EncodingHelper.LosslessAudioCodecs.Contains(codec, StringComparison.OrdinalIgnoreCase)
            || codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // ffprobe profiles: "DTS-HD MA", "DTS-HD MA + DTS:X", "DTS-HD MA + DTS:X IMAX".
        return IsDts(codec) && (stream.Profile?.Contains("MA", StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Picks the audio track to play: among the tracks in the language of the default (else first) audio
    /// track, ignoring commentaries when possible, the lossless one with the most channels; without lossless,
    /// the lossy one with the most channels and highest bitrate.
    /// </summary>
    /// <param name="streams">All streams of the media source.</param>
    /// <returns>The chosen audio stream, or <c>null</c> when there is no audio.</returns>
    public static MediaStream? SelectBest(IEnumerable<MediaStream>? streams)
    {
        var audio = (streams ?? []).Where(s => s.Type == MediaStreamType.Audio).ToList();
        if (audio.Count == 0)
        {
            return null;
        }

        var reference = audio.FirstOrDefault(s => s.IsDefault) ?? audio[0];
        var candidates = audio.Where(s => string.Equals(NormalizeLanguage(s.Language), NormalizeLanguage(reference.Language), StringComparison.OrdinalIgnoreCase)).ToList();
        var nonCommentary = candidates.Where(s => !IsCommentary(s)).ToList();
        if (nonCommentary.Count > 0)
        {
            candidates = nonCommentary;
        }

        return candidates
            .OrderByDescending(IsLossless)
            .ThenByDescending(s => s.Channels ?? 0)
            .ThenByDescending(CodecRank)
            .ThenByDescending(s => s.BitRate ?? 0)
            .ThenBy(s => s.Index)
            .First();
    }

    /// <summary>
    /// Removes every audio stream but the chosen one from a media source and makes it the default.
    /// </summary>
    /// <param name="mediaSource">The media source (its stream list is replaced, the stream objects are not modified).</param>
    /// <returns>The chosen audio stream, or <c>null</c> when there is no audio.</returns>
    public static MediaStream? ApplyTo(MediaSourceInfo mediaSource)
    {
        var best = SelectBest(mediaSource.MediaStreams);
        if (best is null)
        {
            return null;
        }

        mediaSource.MediaStreams = mediaSource.MediaStreams.Where(s => s.Type != MediaStreamType.Audio || s.Index == best.Index).ToList();
        mediaSource.DefaultAudioStreamIndex = best.Index;
        return best;
    }

    private static int CodecRank(MediaStream stream)
    {
        var codec = stream.Codec ?? string.Empty;
        if (IsLossless(stream))
        {
            return codec.ToLowerInvariant() switch
            {
                "truehd" or "mlp" => 4,
                _ when IsDts(codec) => 3,
                "flac" => 2,
                _ => 1
            };
        }

        if (IsDts(codec) && (stream.Profile?.Contains("HRA", StringComparison.Ordinal) ?? false))
        {
            // DTS-HD High Resolution usually has no bitrate in the container; rank it above core DTS.
            return 5;
        }

        return codec.ToLowerInvariant() switch
        {
            "eac3" => 4,
            "dts" or "dca" => 3,
            "ac3" => 2,
            _ => 0
        };
    }

    private static bool IsDts(string codec)
        => string.Equals(codec, "dts", StringComparison.OrdinalIgnoreCase) || string.Equals(codec, "dca", StringComparison.OrdinalIgnoreCase);

    private static bool IsCommentary(MediaStream stream)
        => (stream.Title?.Contains("commentary", StringComparison.OrdinalIgnoreCase) ?? false)
            || (stream.Comment?.Contains("commentary", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string NormalizeLanguage(string? language)
        => string.IsNullOrWhiteSpace(language) || string.Equals(language, "und", StringComparison.OrdinalIgnoreCase) ? string.Empty : language;
}
