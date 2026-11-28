using System.Collections.Generic;
using System.IO;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.StreamingDtos;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public class StreamingHelpersTests
{
    private static IServerConfigurationManager CreateConfigManager()
    {
        var transcodePath = Path.Combine(Path.GetTempPath(), "jellyfin-streaming-helpers-test");
        Directory.CreateDirectory(transcodePath);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(Path.GetTempPath());

        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.CommonApplicationPaths).Returns(appPaths.Object);
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions
        {
            TranscodingTempPath = transcodePath
        });
        return config.Object;
    }

    private static StreamState CreateVideoState(
        string outputVideoCodec,
        string? videoProfile,
        int? videoBitrate = 71444815,
        int? maxWidth = 3840,
        int? maxHeight = 2160,
        string outputAudioCodec = "aac",
        Dictionary<string, string?>? extraStreamOptions = null)
    {
        var streamOptions = new Dictionary<string, string?>();
        if (videoProfile is not null)
        {
            var codecKey = string.Equals(outputVideoCodec, "copy", System.StringComparison.OrdinalIgnoreCase)
                ? "hevc-profile"
                : outputVideoCodec + "-profile";
            streamOptions[codecKey] = videoProfile;
        }

        if (extraStreamOptions is not null)
        {
            foreach (var kvp in extraStreamOptions)
            {
                streamOptions[kvp.Key] = kvp.Value;
            }
        }

        var request = new HlsVideoRequestDto
        {
            DeviceId = "chrome-device",
            PlaySessionId = "session-1",
            AudioStreamIndex = 1,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            StreamOptions = streamOptions
        };

        return new StreamState(
            new Mock<IMediaSourceManager>().Object,
            TranscodingJobType.Hls,
            new Mock<ITranscodeManager>().Object)
        {
            Request = request,
            MediaSource = new MediaSourceInfo(),
            MediaPath = "/media/movies/dad/Fury (2014)/Fury (2014).mkv",
            UserAgent = "Mozilla/5.0 Chrome/152.0.0.0",
            VideoStream = new MediaStream
            {
                Type = MediaStreamType.Video,
                Codec = "hevc",
                BitRate = 71444815,
                Width = 3840,
                Height = 2160
            },
            OutputVideoCodec = outputVideoCodec,
            OutputVideoBitrate = videoBitrate,
            OutputAudioCodec = outputAudioCodec
        };
    }

    [Fact]
    public void GetOutputFilePath_SameVariantDifferentSegmentOptions_Matches()
    {
        var config = CreateConfigManager();
        using var initSeg = CreateVideoState("copy", "main10", extraStreamOptions: new Dictionary<string, string?>
        {
            ["runtimeTicks"] = "0",
            ["actualSegmentLengthTicks"] = "0"
        });
        using var mediaSeg = CreateVideoState("copy", "main10", extraStreamOptions: new Dictionary<string, string?>
        {
            ["runtimeTicks"] = "17026590000",
            ["actualSegmentLengthTicks"] = "17520000"
        });

        var p1 = StreamingHelpers.GetOutputFilePath(initSeg, ".mp4", config, "chrome-device", "session-1");
        var p2 = StreamingHelpers.GetOutputFilePath(mediaSeg, ".mp4", config, "chrome-device", "session-1");

        Assert.Equal(p1, p2);
    }

    [Fact]
    public void GetOutputFilePath_HevcCopyVsH264FallbackSameSession_Differs()
    {
        var config = CreateConfigManager();
        using var hevcCopy = CreateVideoState("copy", "main10");
        using var h264Fallback = CreateVideoState("h264", null);

        var p1 = StreamingHelpers.GetOutputFilePath(hevcCopy, ".mp4", config, "chrome-device", "session-1");
        var p2 = StreamingHelpers.GetOutputFilePath(h264Fallback, ".mp4", config, "chrome-device", "session-1");

        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void GetOutputFilePath_HevcHdr10VsHevcSdrFallbackSameSession_Differs()
    {
        var config = CreateConfigManager();
        using var hevcHdr10 = CreateVideoState("hevc", "main10");
        using var hevcSdr = CreateVideoState("hevc", "main");

        var p1 = StreamingHelpers.GetOutputFilePath(hevcHdr10, ".mp4", config, "chrome-device", "session-1");
        var p2 = StreamingHelpers.GetOutputFilePath(hevcSdr, ".mp4", config, "chrome-device", "session-1");

        Assert.NotEqual(p1, p2);
    }

    [Fact]
    public void GetOutputFilePath_DifferentAbrBitratesSameSession_Differs()
    {
        var config = CreateConfigManager();
        using var high = CreateVideoState("hevc", "main10", videoBitrate: 40000000);
        using var low = CreateVideoState("hevc", "main10", videoBitrate: 20000000);

        var p1 = StreamingHelpers.GetOutputFilePath(high, ".mp4", config, "chrome-device", "session-1");
        var p2 = StreamingHelpers.GetOutputFilePath(low, ".mp4", config, "chrome-device", "session-1");

        Assert.NotEqual(p1, p2);
    }
}
