using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding
{
    public class SingleAudioTrackTests
    {
        private static MediaStream A(int index, string codec, int channels, string lang, int? bitrate = null, string? profile = null, bool isDefault = false, string? title = null)
            => new MediaStream { Type = MediaStreamType.Audio, Index = index, Codec = codec, Channels = channels, Language = lang, BitRate = bitrate, Profile = profile, IsDefault = isDefault, Title = title };

        private static MediaStream V() => new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "hevc" };

        [Fact]
        public void SelectBest_PrefersLosslessOverLossyDefault()
        {
            // 10,000 BC: the default track is AC-3, the TrueHD track is not default.
            var streams = new List<MediaStream> { V(), A(1, "ac3", 6, "eng", 640000, isDefault: true), A(2, "truehd", 6, "eng"), A(4, "ac3", 6, "fre", 640000) };
            Assert.Equal(2, SingleAudioTrack.SelectBest(streams)!.Index);
        }

        [Fact]
        public void SelectBest_StaysInDefaultLanguage()
        {
            // Akira: Japanese TrueHD is the default; English TrueHD exists too.
            var streams = new List<MediaStream> { V(), A(1, "truehd", 6, "jpn", isDefault: true), A(2, "ac3", 6, "jpn", 448000), A(3, "truehd", 6, "eng") };
            Assert.Equal(1, SingleAudioTrack.SelectBest(streams)!.Index);
        }

        [Fact]
        public void SelectBest_DtsHdMaBeatsCoreAndPcmStereo_MostChannels()
        {
            // Memories of Murder: PCM 2.0 default, DTS-HD MA 7.1, DTS core 5.1.
            var streams = new List<MediaStream> { V(), A(1, "pcm_s24le", 2, "kor", 2304000, isDefault: true), A(2, "dts", 8, "kor", profile: "DTS-HD MA"), A(3, "dts", 6, "kor", 1536000, "DTS") };
            Assert.Equal(2, SingleAudioTrack.SelectBest(streams)!.Index);
        }

        [Fact]
        public void SelectBest_NoLossless_PicksEac3OverEqualDts()
        {
            // Sahara: E-AC-3 1536k vs DTS 1536k, both 5.1.
            var streams = new List<MediaStream> { V(), A(1, "eac3", 6, "eng", 1536000, isDefault: true), A(2, "dts", 6, "eng", 1536000, "DTS"), A(5, "ac3", 2, "eng", 192000) };
            Assert.Equal(1, SingleAudioTrack.SelectBest(streams)!.Index);
        }

        [Fact]
        public void SelectBest_SkipsCommentary()
        {
            var streams = new List<MediaStream> { V(), A(1, "flac", 2, "eng", isDefault: true, title: "Commentary with the director"), A(2, "ac3", 6, "eng", 640000) };
            Assert.Equal(2, SingleAudioTrack.SelectBest(streams)!.Index);
        }

        [Fact]
        public void SelectBest_NoAudio_ReturnsNull()
        {
            Assert.Null(SingleAudioTrack.SelectBest(new List<MediaStream> { V() }));
        }

        [Fact]
        public void ApplyTo_KeepsOnlyChosenAudio()
        {
            var source = new MediaSourceInfo
            {
                MediaStreams = new List<MediaStream> { V(), A(1, "truehd", 8, "eng", profile: "Dolby TrueHD + Dolby Atmos", isDefault: true), A(2, "ac3", 6, "eng", 640000), new MediaStream { Type = MediaStreamType.Subtitle, Index = 3 } },
                DefaultAudioStreamIndex = 2
            };

            var best = SingleAudioTrack.ApplyTo(source);

            Assert.Equal(1, best!.Index);
            Assert.Equal(1, source.DefaultAudioStreamIndex);
            Assert.Equal(new[] { 0, 1, 3 }, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(source.MediaStreams, s => s.Index)));
        }

        [Theory]
        [InlineData(null, 1024000)]
        [InlineData("", 1024000)]
        [InlineData("1536k", 1536000)]
        [InlineData("1.5M", 1500000)]
        [InlineData("768000", 768000)]
        [InlineData("10", 192000)]
        [InlineData("99M", 6144000)]
        [InlineData("junk", 1024000)]
        public void ParseBitrate(string? value, int expected)
        {
            Assert.Equal(expected, SingleAudioTrack.ParseBitrate(value));
        }

        [Fact]
        public void AppliesToClient_SubstringOfClientOrDevice()
        {
            var clients = SingleAudioTrack.ParseClients(" Google TV streamer , Foo");
            Assert.True(SingleAudioTrack.AppliesToClient(clients, "Jellyfin Android TV", "Basement Google TV Streamer"));
            Assert.False(SingleAudioTrack.AppliesToClient(clients, "Kodi", "CoreELEC"));
            Assert.False(SingleAudioTrack.AppliesToClient(SingleAudioTrack.ParseClients(null), "Jellyfin Android TV", "Basement Google TV streamer"));
        }

        [Fact]
        public void Options_OneBestPerLanguage_PlusDistinctLosslessLayouts()
        {
            // The Great Escape: DTS-HD MA mono (default) + core, DTS-HD MA 5.1 + core, two AC-3 stereo; plus a French AC-3.
            var streams = new List<MediaStream>
            {
                V(), A(1, "dts", 1, "eng", profile: "DTS-HD MA", isDefault: true), A(2, "dts", 1, "eng", 768000, "DTS"),
                A(3, "dts", 6, "eng", profile: "DTS-HD MA"), A(4, "dts", 6, "eng", 1536000, "DTS"), A(5, "ac3", 2, "eng", 192000),
                A(6, "ac3", 6, "fre", 640000), A(7, "ac3", 2, "fre", 192000)
            };
            var options = SingleAudioTrack.SelectOptions(streams, null);
            Assert.Equal(new[] { 1, 3, 6 }, options.Select(s => s.Index));
            Assert.Equal(3, SingleAudioTrack.Resolve(streams, null, null, null).Chosen!.Index);
        }

        [Fact]
        public void Options_OneTrackPerChannelCount()
        {
            // Nightcrawler: TrueHD 7.1, DTS-HD MA 5.1, three DTS-HD MA stereo (one mix, two commentaries untagged).
            var streams = new List<MediaStream> { V(), A(1, "truehd", 8, "eng", isDefault: true), A(3, "dts", 6, "eng", profile: "DTS-HD MA"), A(5, "dts", 2, "eng", profile: "DTS-HD MA"), A(7, "dts", 2, "eng", profile: "DTS-HD MA") };
            Assert.Equal(new[] { 1, 3, 5 }, SingleAudioTrack.SelectOptions(streams, null).Select(s => s.Index));
        }

        [Fact]
        public void Resolve_PreferredLanguageThenOriginalThenDefault()
        {
            // Spirited Away: English DTS-HD MA is the default, Japanese DTS-HD MA exists, French AC-3.
            var streams = new List<MediaStream> { V(), A(1, "dts", 6, "eng", profile: "DTS-HD MA", isDefault: true), A(3, "ac3", 6, "fre", 640000), A(4, "dts", 6, "jpn", profile: "DTS-HD MA") };
            var path = WriteHints("/media/movies/x/spirited.mkv", "jpn", string.Empty);
            Assert.Equal(4, SingleAudioTrack.Resolve(streams, path, null, null).Chosen!.Index);
            Assert.Equal(4, SingleAudioTrack.Resolve(streams, path, string.Empty, null).Chosen!.Index);
            Assert.Equal(1, SingleAudioTrack.Resolve(streams, path, "eng", null).Chosen!.Index);
            Assert.Equal(3, SingleAudioTrack.Resolve(streams, path, null, 3).Chosen!.Index);
            // Not offered (or nonexistent) requested index -> the default.
            Assert.Equal(4, SingleAudioTrack.Resolve(streams, path, null, 9).Chosen!.Index);
            // No hints: the default track's language.
            Assert.Equal(1, SingleAudioTrack.Resolve(streams, "/other.mkv", null, null).Chosen!.Index);
            // Preferred language missing from the file -> original.
            Assert.Equal(4, SingleAudioTrack.Resolve(streams, path, "kor", null).Chosen!.Index);
        }

        [Fact]
        public void Options_HintsSkipSameMixAndCommentary()
        {
            var streams = new List<MediaStream> { V(), A(1, "truehd", 8, "eng", isDefault: true), A(3, "dts", 6, "eng", profile: "DTS-HD MA"), A(5, "dts", 2, "eng", profile: "DTS-HD MA"), A(7, "dts", 2, "eng", profile: "DTS-HD MA") };
            var path = WriteHints("/media/movies/x/night.mkv", "eng", "\"3\": \"same-mix\", \"5\": \"commentary\"");
            Assert.Equal(new[] { 1, 7 }, SingleAudioTrack.SelectOptions(streams, path).Select(s => s.Index));
        }

        [Fact]
        public void ApplyOptionsTo_FiltersAndSetsDefault()
        {
            var source = new MediaSourceInfo
            {
                Path = "/none.mkv",
                MediaStreams = new List<MediaStream> { V(), A(1, "ac3", 6, "deu", 640000, isDefault: true), A(2, "ac3", 6, "eng", 640000), A(3, "ac3", 2, "eng", 192000) }
            };
            var chosen = SingleAudioTrack.ApplyOptionsTo(source, "eng", null);
            Assert.Equal(2, chosen!.Index);
            Assert.Equal(2, source.DefaultAudioStreamIndex);
            Assert.Equal(new[] { 0, 1, 2 }, source.MediaStreams.Select(s => s.Index));
        }

        [Fact]
        public void Hints_Parse()
        {
            var h = AudioTrackHints.Parse("{\"files\": {\"/a.mkv\": {\"orig\": \"kor\", \"skip\": {\"4\": \"same-mix\"}}, \"/b.mkv\": {}}}");
            Assert.Equal("kor", h["/a.mkv"].OriginalLanguage);
            Assert.Contains(4, h["/a.mkv"].Skip);
            Assert.Null(h["/b.mkv"].OriginalLanguage);
        }

        private static string WriteHints(string mediaPath, string orig, string skip)
        {
            // Tests in one class run sequentially, so the process-wide env var is safe here.
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audio-hints-" + System.Guid.NewGuid().ToString("N") + ".json");
            System.IO.File.WriteAllText(file, "{\"files\": {\"" + mediaPath + "\": {\"orig\": \"" + orig + "\", \"skip\": {" + skip + "}}}}");
            System.Environment.SetEnvironmentVariable("JELLYFIN_AUDIO_TRACK_HINTS", file);
            return mediaPath;
        }
    }
}
