FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build-env
WORKDIR /app

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:7.0
RUN printf "deb http://deb.debian.org/debian bullseye-backports main contrib non-free\ndeb-src http://deb.debian.org/debian bullseye-backports main contrib non-free" > /etc/apt/sources.list.d/backports.list
RUN apt update && apt install -y yt-dlp ffmpeg python3 gallery-dl && apt clean && apt autoremove
WORKDIR /app
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "RaterBot.dll"]
ENV DOTNET_ReadyToRun=0