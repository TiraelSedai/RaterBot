FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["RaterBot/RaterBot.csproj", "RaterBot/"]
COPY ["RaterBot.Database/RaterBot.Database.csproj", "RaterBot.Database/"]
RUN dotnet restore "RaterBot/RaterBot.csproj" -r linux-x64
COPY . .
WORKDIR "/src/RaterBot"
RUN dotnet publish "RaterBot.csproj" -c Release -r linux-x64 -o /publish --self-contained false && \
    cp "$(find /root/.nuget/packages/opencvsharp4.runtime.linux-x64 -name libOpenCvSharpExtern.so -print -quit)" /publish/libOpenCvSharpExtern.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl ffmpeg libgomp1 tesseract-ocr tesseract-ocr-eng tesseract-ocr-rus; \
    echo "deb http://archive.ubuntu.com/ubuntu jammy main universe" > /etc/apt/sources.list.d/jammy.list; \
    apt-get update; \
    apt-get install -y --no-install-recommends libtesseract4 libavcodec58 libavformat58 libavutil56 libswscale5 libtiff5 libgtk2.0-0 libopenexr25; \
    rm -f /etc/apt/sources.list.d/jammy.list; \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /publish .
# extremely dirty hack to have linux libraries in place instead of windows libraries
RUN rm -f /app/onnxruntime.dll /app/onnxruntime_providers_shared.dll && \
    ln -s /app/libonnxruntime.so /app/onnxruntime.dll && \
    ln -s /app/libonnxruntime_providers_shared.so /app/onnxruntime_providers_shared.dll
ENV LD_LIBRARY_PATH="/app:${LD_LIBRARY_PATH}"

RUN mkdir -p /app/models && \
    curl -fL -o /app/models/vision_model_quantized.onnx \
    https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/main/onnx/vision_model_quantized.onnx && \
    curl -fL -o /app/models/frozen_east_text_detection.pb \
    https://github.com/oyyd/frozen_east_text_detection.pb/raw/master/frozen_east_text_detection.pb

ENV UV_TOOL_BIN_DIR="/usr/local/bin"
ENV UV_NO_CACHE=1
RUN --mount=from=ghcr.io/astral-sh/uv:latest,source=/uv,target=/bin/uv \
    uv tool install yt-dlp && uv tool install gallery-dl

ENTRYPOINT ["./RaterBot"]
