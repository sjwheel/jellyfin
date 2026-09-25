FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS builder
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.Server --configuration Release -r linux-x64 --self-contained true --output /out

FROM jellyfin/jellyfin:10.11.8
COPY --from=builder /out/ /jellyfin/
