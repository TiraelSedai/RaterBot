FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["RaterBot/RaterBot.csproj", "RaterBot/"]
COPY ["RaterBot.Database/RaterBot.Database.csproj", "RaterBot.Database/"]
RUN dotnet restore "RaterBot/RaterBot.csproj" -r linux-x64
COPY . .
WORKDIR "/src/RaterBot"
RUN dotnet publish "RaterBot.csproj" -c Release -o /app/publish -r linux-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt update && apt install -y apt-transport-https curl
RUN apt install -y ffmpeg libgomp1 && apt clean && apt autoremove
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/models && \
    curl -L -o /app/models/vision_model_quantized.onnx \
    https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx

ENV UV_TOOL_BIN_DIR="/usr/local/bin"
ENV UV_NO_CACHE=1
RUN --mount=from=ghcr.io/astral-sh/uv:latest,source=/uv,target=/bin/uv \
    uv tool install yt-dlp && uv tool install gallery-dl

ENTRYPOINT ["./RaterBot"]
