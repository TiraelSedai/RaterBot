FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build-env
WORKDIR /app

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:6.0
RUN apt-get update && apt-get install -y --force-yes curl ffmpeg python3 && apt-get clean && apt-get autoremove
RUN curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && chmod a+rx /usr/local/bin/yt-dlp
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "RaterBot.dll"]
ENV DOTNET_TieredPGO=1
ENV DOTNET_TC_QuickJitForLoops=1
ENV DOTNET_ReadyToRun=0