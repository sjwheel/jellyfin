using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Controller.Tests.MediaEncoding;

// Diagnostic: reproduces the Google TV Streamer HLS request for Kubo (DV P7.6 FEL).
public class DoviP81CopyDecisionTests
{
    private readonly ITestOutputHelper _out;

    public DoviP81CopyDecisionTests(ITestOutputHelper output) => _out = output;

    private static MediaStream Kubo() => new MediaStream
    {
        Type = MediaStreamType.Video, Codec = "hevc", Profile = "Main 10", Level = 153, BitDepth = 10,
        Width = 3840, Height = 2160, PixelFormat = "yuv420p10le", BitRate = 95630672,
        RealFrameRate = 24, AverageFrameRate = 24, IsInterlaced = false, IsAVC = false, Index = 0,
        ColorSpace = "bt2020nc", ColorTransfer = "smpte2084", ColorPrimaries = "bt2020",
        DvVersionMajor = 1, DvVersionMinor = 0, DvProfile = 7, DvLevel = 6,
        RpuPresentFlag = 1, ElPresentFlag = 1, BlPresentFlag = 1, DvBlSignalCompatibilityId = 6,
    };

    private static EncodingJobInfo State(MediaStream v)
    {
        var req = new VideoRequestDto
        {
            VideoCodec = "hevc,h264", AudioCodec = "aac,ac3,eac3,mp3", VideoBitRate = 196118436,
            MaxFramerate = 24, MaxWidth = 4096, MaxHeight = 2304, SegmentContainer = "ts",
            SubtitleMethod = SubtitleDeliveryMethod.Encode,
        };
        req.StreamOptions["hevc-level"] = "153";
        req.StreamOptions["hevc-videobitdepth"] = "10";
        req.StreamOptions["hevc-profile"] = "main10";
        req.StreamOptions["hevc-rangetype"] = "Unknown,SDR,HDR10,HLG,DOVI,DOVIWithHDR10,DOVIWithHLG,DOVIWithSDR,DOVIWithHDR10Plus";
        return new EncodingJobInfo(TranscodingJobType.Hls)
        {
            BaseRequest = req,
            VideoStream = v,
            SupportedVideoCodecs = new[] { "hevc", "h264" },
            MediaSource = new MediaSourceInfo { Container = "mkv", Bitrate = 106232236, MediaStreams = new List<MediaStream> { v } },
        };
    }

    private static EncodingHelper Helper(bool conversionEnabled)
    {
        var enc = new Mock<IMediaEncoder>();
        enc.Setup(e => e.SupportsBitStreamFilterWithOption(It.IsAny<BitStreamFilterOptionType>())).Returns(true);
        var cfg = new Mock<IConfiguration>();
        cfg.Setup(c => c["FFmpeg:doviP81Conversion"]).Returns(conversionEnabled ? "true" : "false");
        return new EncodingHelper(Mock.Of<IApplicationPaths>(), enc.Object, Mock.Of<ISubtitleEncoder>(), cfg.Object, Mock.Of<IServerConfigurationManager>(), Mock.Of<IPathManager>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CopyDecision(bool conversionEnabled)
    {
        var v = Kubo();
        var state = State(v);
        var h = Helper(conversionEnabled);
        _out.WriteLine($"conversion={conversionEnabled} rangeType={v.VideoRangeType} converted={h.IsDoviConvertedToP81(state)} removed={h.IsDoviRemoved(state)} canCopy={h.CanStreamCopyVideo(state, v)} bsf={h.GetBitStreamArgs(state, MediaStreamType.Video)}");
    }
}
