# Patched jellyfin-ffmpeg (github.com/sjwheel/jellyfin-ffmpeg, adds dovi_rpu convert=p81).
# Tag = fork commit; built by Tekton, recorded in sjwheel/ansi pins/jellyfin-ffmpeg-deb.txt.
ARG FFMPEG_DEB=harbor.sjwheel.net/jellyfin/jellyfin-ffmpeg-deb:b7a17ab16f6ade3b40084bf670824dcd0d2836fb

FROM ${FFMPEG_DEB} AS ffmpeg-deb

FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS builder
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.Server --configuration Release -r linux-x64 --self-contained true --output /out

FROM jellyfin/jellyfin:10.11.8
COPY --from=ffmpeg-deb /jellyfin-ffmpeg7_*.deb /tmp/
RUN dpkg -i /tmp/jellyfin-ffmpeg7_*.deb \
 && rm -f /tmp/jellyfin-ffmpeg7_*.deb \
 && /usr/lib/jellyfin-ffmpeg/ffmpeg -hide_banner -h bsf=dovi_rpu | grep -q p81
COPY --from=builder /out/ /jellyfin/
