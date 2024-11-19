FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["RaterBot/RaterBot.csproj", "RaterBot/"]
COPY ["RaterBot.Database/RaterBot.Database.csproj", "RaterBot.Database/"]
RUN dotnet restore "RaterBot/RaterBot.csproj" -r linux-x64
COPY . .
WORKDIR "/src/RaterBot"
RUN dotnet publish "RaterBot.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:9.0
RUN apt update && apt install -y apt-transport-https
RUN apt install -y yt-dlp ffmpeg python3 gallery-dl && apt clean && apt autoremove
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["./RaterBot"]
# totally not USER app