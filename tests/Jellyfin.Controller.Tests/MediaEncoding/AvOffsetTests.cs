using Jellyfin.Data.Enums;
using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class AvOffsetTests
{
    private const string Json = """
        {"clients": ["Google TV streamer"],
         "rules": [
           {"audio_delay_ms": 5},
           {"res": "2160p", "audio_delay_ms": 10},
           {"res": "2160p", "range": "dv", "fps": "23.976", "audio_delay_ms": -20.5},
           {"res": "sd", "fps": "29.97", "audio_delay_ms": 30}
         ]}
        """;

    [Theory]
    [InlineData(3840, 2160, VideoRangeType.DOVIWithHDR10, 23.976025f, -20.5)]
    [InlineData(3840, 2160, VideoRangeType.HDR10, 23.976025f, 10)]
    [InlineData(3840, 1600, VideoRangeType.DOVI, 24f, 10)]
    [InlineData(1920, 1080, VideoRangeType.SDR, 23.976025f, 5)]
    [InlineData(720, 480, VideoRangeType.SDR, 29.97003f, 30)]
    [InlineData(720, 576, VideoRangeType.SDR, 25f, 5)]
    public void PicksMostSpecificRule(int w, int h, VideoRangeType range, float fps, double expected)
    {
        var config = AvOffset.Parse(Json);
        Assert.Equal(expected, AvOffset.GetDelayMs(config, "Jellyfin Android TV", "Basement Google TV Streamer", w, h, range, fps));
    }

    [Fact]
    public void OtherClientsGetNothing()
    {
        var config = AvOffset.Parse(Json);
        Assert.Equal(0, AvOffset.GetDelayMs(config, "Kodi", "CoreELEC", 3840, 2160, VideoRangeType.DOVI, 23.976f));
    }

    [Theory]
    [InlineData(23.976025f, "23.976")]
    [InlineData(24f, "24")]
    [InlineData(59.94006f, "59.94")]
    [InlineData(12.5f, "other")]
    public void FpsClasses(float fps, string expected) => Assert.Equal(expected, AvOffset.FpsClass(fps));

    [Theory]
    [InlineData(100.0, "asetpts=PTS+0.100/TB")]
    [InlineData(-20.5, "asetpts=PTS-0.021/TB")]
    public void Filter(double ms, string expected) => Assert.Equal(expected, AvOffset.BuildFilter(ms));
}
