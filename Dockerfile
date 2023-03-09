FROM mcr.microsoft.com/dotnet/sdk:8.0-preview AS build-env
WORKDIR /app

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
WORKDIR RaterBot
RUN dotnet publish -o /out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0-preview
RUN apt update && apt install -y yt-dlp ffmpeg python3 gallery-dl && apt clean && apt autoremove
WORKDIR /app
COPY --from=build-env /out .
ENTRYPOINT ["dotnet", "RaterBot.dll"]
USER app