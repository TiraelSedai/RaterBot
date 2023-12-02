FROM mcr.microsoft.com/dotnet/runtime:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["RaterBot/RaterBot.csproj", "RaterBot/"]
COPY ["RaterBot.Database/RaterBot.Database.csproj", "RaterBot.Database/"]
RUN dotnet restore "RaterBot/RaterBot.csproj"
COPY . .
WORKDIR "/src/RaterBot"
RUN dotnet build "RaterBot.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "RaterBot.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
RUN apt update && apt install -y yt-dlp ffmpeg python3 gallery-dl && apt clean && apt autoremove
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "RaterBot.dll"]
# totally not USER app