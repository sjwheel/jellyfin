FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS builder
WORKDIR /src
COPY . .
RUN dotnet publish Jellyfin.Server --configuration Release -r linux-x64 --self-contained true --output /out

FROM jellyfin/jellyfin:unstable
COPY --from=builder /out/ /jellyfin/
