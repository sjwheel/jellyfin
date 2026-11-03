using MediaBrowser.Controller.MediaEncoding;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding
{
    public class EdlMuteTests
    {
        [Fact]
        public void Parse_KeepsOnlyMuteLines_AnyWhitespace()
        {
            var windows = EdlMute.Parse(new[]
            {
                "2865 2971.5 0",
                "488.936\t489.436\t1",
                "375.762  376.162 1",
                "garbage",
                "10 5 1",
            });

            Assert.Equal(new[] { new EdlMuteWindow(375.762, 376.162), new EdlMuteWindow(488.936, 489.436) }, windows);
        }

        [Fact]
        public void BuildFilter_JoinsWindowsAndAppliesOffset()
        {
            var windows = new[] { new EdlMuteWindow(375.762, 376.162), new EdlMuteWindow(488.936, 489.436) };

            Assert.Equal("volume=enable='between(t,375.762,376.162)+between(t,488.936,489.436)':volume=0", EdlMute.BuildFilter(windows, 0));
            Assert.Equal("volume=enable='between(t,275.762,276.162)+between(t,388.936,389.436)':volume=0", EdlMute.BuildFilter(windows, 100));
        }
    }
}
