using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using System.Threading.Channels;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RaterBot.Database;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace RaterBot;

internal interface IVectorSearchService
{
    void Process(string fileId, VectorMediaKind mediaKind, Chat chat, MessageId messageId);
    void ProcessLocalMotion(string localMediaPath, Chat chat, MessageId messageId);
}

internal sealed partial class VectorSearchService : IDisposable, IVectorSearchService
{
    private const float SimilarityThreshold = 0.96f;
    private const int ImageSize = 224;
    private const int EmbeddingDimension = 512;
    private const int MotionScanWindowSeconds = 15;
    private const int MotionMaxKeyframes = 5;
    private const int MotionFfmpegTimeoutMs = 30_000;

    private static readonly DateTime QuantCutoff = new(2026, 1, 29, 10, 0, 0, DateTimeKind.Utc);

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromDays(30);
    private readonly ILogger<VectorSearchService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InferenceSession? _session;
    private readonly bool _isEnabled;

    private abstract record WorkItem(VectorMediaKind MediaKind, Chat Chat, MessageId MessageId);

    private sealed record TelegramWorkItem(string FileId, VectorMediaKind MediaKind, Chat Chat, MessageId MessageId)
        : WorkItem(MediaKind, Chat, MessageId);

    private sealed record LocalMotionWorkItem(string MediaPath, string TempDir, Chat Chat, MessageId MessageId)
        : WorkItem(VectorMediaKind.Motion, Chat, MessageId);

    private record ProcessedPost(IReadOnlyList<float[]> Embeddings);

    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public VectorSearchService(ILogger<VectorSearchService> logger, ITelegramBotClient bot, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _bot = bot;
        _scopeFactory = scopeFactory;

        var clipModelPath = FindModelPath("vision_model_quantized.onnx");
        if (clipModelPath != null)
        {
            _logger.LogInformation("VectorSearchService: trying to load model from {ModelPath}", clipModelPath);
            var sw = Stopwatch.StartNew();
            try
            {
                var loadTask = Task.Run(() => new InferenceSession(clipModelPath));
                if (!loadTask.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Model loading exceeded 5 seconds timeout");

                _session = loadTask.Result;
                _isEnabled = true;
                _logger.LogInformation(
                    "VectorSearchService enabled, model loaded from {ModelPath} in {ElapsedMs}ms",
                    clipModelPath,
                    sw.ElapsedMilliseconds
                );
                Task.Run(WorkLoop);
            }
            catch (Exception ex)
            {
                _isEnabled = false;
                _logger.LogError(
                    ex,
                    "VectorSearchService disabled: failed to load model from {ModelPath} after {ElapsedMs}ms",
                    clipModelPath,
                    sw.ElapsedMilliseconds
                );
            }
        }
        else
        {
            _isEnabled = false;
            _logger.LogWarning("VectorSearchService disabled: vision_model_quantized.onnx not found");
        }

        _logger.LogDebug("VectorSearchService ctor end");
    }

    public void Process(string fileId, VectorMediaKind mediaKind, Chat chat, MessageId messageId)
    {
        if (_isEnabled && !string.IsNullOrWhiteSpace(fileId))
            _channel.Writer.TryWrite(new TelegramWorkItem(fileId, mediaKind, chat, messageId));
    }

    public void ProcessLocalMotion(string localMediaPath, Chat chat, MessageId messageId)
    {
        if (!_isEnabled)
            return;

        if (string.IsNullOrWhiteSpace(localMediaPath) || !File.Exists(localMediaPath))
        {
            _logger.LogWarning("Skipping local motion processing because the source file is unavailable: {Path}", localMediaPath);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"raterbot-motion-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var copiedMediaPath = Path.Combine(tempDir, "input_media.bin");
            File.Copy(localMediaPath, copiedMediaPath, overwrite: true);

            if (!_channel.Writer.TryWrite(new LocalMotionWorkItem(copiedMediaPath, tempDir, chat, messageId)))
            {
                _logger.LogWarning("Unable to enqueue local motion work item");
                DeleteDirectoryWithRetries(tempDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to stage local motion media for vector search");
            DeleteDirectoryWithRetries(tempDir);
        }
    }

    private async Task WorkLoop()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                var processed = await CalculateAndWriteFeatures(item);
                await CompareToExistingPosts(item, processed);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to process item from vector search queue");
            }
        }
    }

    private async Task<ProcessedPost?> CalculateAndWriteFeatures(WorkItem item) =>
        item switch
        {
            TelegramWorkItem telegramItem when item.MediaKind == VectorMediaKind.Image => await CalculateAndWriteImageFeatures(
                telegramItem
            ),
            TelegramWorkItem telegramItem when item.MediaKind == VectorMediaKind.Motion => await CalculateAndWriteMotionFeatures(
                telegramItem
            ),
            LocalMotionWorkItem localMotionItem => await CalculateAndWriteMotionFeatures(localMotionItem),
            _ => null,
        };

    private async Task CompareToExistingPosts(WorkItem item, ProcessedPost? processed)
    {
        if (processed == null)
            return;

        if (item.MediaKind == VectorMediaKind.Motion)
        {
            await CompareToExistingMotionPostsByClip(item, processed.Embeddings);
            return;
        }

        await CompareToExistingImagePostsByClip(item, processed.Embeddings[0]);
    }

    private async Task CompareToExistingImagePostsByClip(WorkItem item, float[] embedding)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var now = DateTime.UtcNow;
        var imageKind = (int)VectorMediaKind.Image;
        var candidates = await db
            .Posts.Where(x =>
                x.ChatId == item.Chat.Id
                && x.MessageId != item.MessageId.Id
                && x.ClipEmbedding != null
                && x.Timestamp > now - _deduplicationWindow
                && (x.VectorMediaKind == null || x.VectorMediaKind == imageKind)
            )
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new
            {
                x.MessageId,
                x.ClipEmbedding,
                x.Timestamp,
            })
            .ToListAsync();
        _logger.LogDebug("Found {Count} CLIP duplicate candidates", candidates.Count);

        foreach (var candidate in candidates)
        {
            var candidateEmbedding = BytesToFloats(candidate.ClipEmbedding!, candidate.Timestamp);
            if (embedding.Length != candidateEmbedding.Length)
            {
                _logger.LogDebug(
                    "Different length vectors. target {EmbeddingLen} candidate {CandidateLne}",
                    embedding.Length,
                    candidateEmbedding.Length
                );
                continue;
            }

            var similarity = TensorPrimitives.CosineSimilarity(embedding, candidateEmbedding);
            if (similarity < SimilarityThreshold)
                continue;

            _logger.LogInformation("Found possible duplicate via CLIP (similarity: {Similarity:F3})", similarity);
            var linkToMessage = TelegramHelper.LinkToMessage(item.Chat, candidate.MessageId);
            _bot.TemporaryReply(item.Chat.Id, item.MessageId, $"Уже было? {linkToMessage}");
            break;
        }
    }

    private async Task CompareToExistingMotionPostsByClip(WorkItem item, IReadOnlyList<float[]> incomingEmbeddings)
    {
        if (incomingEmbeddings.Count < 2)
        {
            _logger.LogDebug("Skipping motion duplicate search due to low keyframe count ({Count})", incomingEmbeddings.Count);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var now = DateTime.UtcNow;
        var motionKind = (int)VectorMediaKind.Motion;
        var candidates = await db
            .Posts.Where(x =>
                x.ChatId == item.Chat.Id
                && x.MessageId != item.MessageId.Id
                && x.ClipEmbedding != null
                && x.Timestamp > now - _deduplicationWindow
                && x.VectorMediaKind == motionKind
            )
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new
            {
                x.MessageId,
                x.Timestamp,
                x.ClipEmbedding,
                x.ClipEmbedding2,
                x.ClipEmbedding3,
                x.ClipEmbedding4,
                x.ClipEmbedding5,
            })
            .ToListAsync();
        _logger.LogDebug("Found {Count} motion CLIP duplicate candidates", candidates.Count);

        foreach (var candidate in candidates)
        {
            var candidateEmbeddings = DecodeEmbeddings(
                candidate.Timestamp,
                candidate.ClipEmbedding,
                candidate.ClipEmbedding2,
                candidate.ClipEmbedding3,
                candidate.ClipEmbedding4,
                candidate.ClipEmbedding5
            );
            if (!HasTwoDistinctFrameMatches(incomingEmbeddings, candidateEmbeddings, SimilarityThreshold))
                continue;

            _logger.LogInformation("Found possible duplicate via motion CLIP");
            var linkToMessage = TelegramHelper.LinkToMessage(item.Chat, candidate.MessageId);
            _bot.TemporaryReply(item.Chat.Id, item.MessageId, $"Уже было? {linkToMessage}");
            break;
        }
    }

    private async Task<ProcessedPost> CalculateAndWriteImageFeatures(TelegramWorkItem item)
    {
        byte[] imageBytes;
        using (var ms = new MemoryStream())
        {
            await _bot.GetInfoAndDownloadFile(item.FileId, ms);
            imageBytes = ms.ToArray();
        }

        using var image = Image.Load<Rgb24>(imageBytes);
        var embedding = GetEmbedding(image);
        var embeddingBytes = FloatsToInt8Bytes(embedding);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        await db
            .Posts.Where(x => x.ChatId == item.Chat.Id && x.MessageId == item.MessageId.Id)
            .Set(x => x.ClipEmbedding, embeddingBytes)
            .Set(x => x.ClipEmbedding2, (byte[]?)null)
            .Set(x => x.ClipEmbedding3, (byte[]?)null)
            .Set(x => x.ClipEmbedding4, (byte[]?)null)
            .Set(x => x.ClipEmbedding5, (byte[]?)null)
            .Set(x => x.VectorMediaKind, (int)VectorMediaKind.Image)
            .UpdateAsync();

        _logger.LogInformation("Image analyzed via CLIP. EmbeddingLength={Length}", embedding.Length);

        return new ProcessedPost([embedding]);
    }

    private async Task<ProcessedPost?> CalculateAndWriteMotionFeatures(WorkItem item)
    {
        var prepared = await PrepareMotionMediaAsync(item);
        if (prepared == null)
            return null;

        var (mediaPath, tempDir) = prepared.Value;
        try
        {
            var keyframePaths = ExtractKeyframePaths(mediaPath, tempDir);
            var embeddings = new List<float[]>(Math.Min(MotionMaxKeyframes, keyframePaths.Count));
            foreach (var keyframePath in keyframePaths)
            {
                using var image = Image.Load<Rgb24>(keyframePath);
                embeddings.Add(GetEmbedding(image));
            }

            var quantized = embeddings.Select(FloatsToInt8Bytes).ToList();
            var first = quantized.Count > 0 ? quantized[0] : null;
            var second = quantized.Count > 1 ? quantized[1] : null;
            var third = quantized.Count > 2 ? quantized[2] : null;
            var fourth = quantized.Count > 3 ? quantized[3] : null;
            var fifth = quantized.Count > 4 ? quantized[4] : null;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();
            await db
                .Posts.Where(x => x.ChatId == item.Chat.Id && x.MessageId == item.MessageId.Id)
                .Set(x => x.ClipEmbedding, first)
                .Set(x => x.ClipEmbedding2, second)
                .Set(x => x.ClipEmbedding3, third)
                .Set(x => x.ClipEmbedding4, fourth)
                .Set(x => x.ClipEmbedding5, fifth)
                .Set(x => x.VectorMediaKind, (int)VectorMediaKind.Motion)
                .UpdateAsync();

            _logger.LogInformation("Motion media analyzed. Keyframes={Count}", embeddings.Count);

            return new ProcessedPost(embeddings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Motion media analysis failed");
            return null;
        }
        finally
        {
            DeleteDirectoryWithRetries(tempDir);
        }
    }

    private async Task<(string MediaPath, string TempDir)?> PrepareMotionMediaAsync(WorkItem item)
    {
        switch (item)
        {
            case LocalMotionWorkItem localItem:
                return (localItem.MediaPath, localItem.TempDir);
            case TelegramWorkItem telegramItem:
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"raterbot-motion-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tempDir);
                var mediaPath = Path.Combine(tempDir, "input_media.bin");
                try
                {
                    await using var stream = File.Create(mediaPath);
                    await _bot.GetInfoAndDownloadFile(telegramItem.FileId, stream);
                    return (mediaPath, tempDir);
                }
                catch
                {
                    DeleteDirectoryWithRetries(tempDir);
                    throw;
                }
            }
            default:
                return null;
        }
    }

    private float[] GetEmbedding(Image<Rgb24> image)
    {
        image.Mutate(x =>
            x.Resize(
                new ResizeOptions
                {
                    Size = new SixLabors.ImageSharp.Size(ImageSize, ImageSize),
                    Mode = ResizeMode.Pad,
                    Position = AnchorPositionMode.Center,
                    PadColor = SixLabors.ImageSharp.Color.Black,
                }
            )
        );

        var inputTensor = new DenseTensor<float>([1, 3, ImageSize, ImageSize]);

        for (var y = 0; y < ImageSize; y++)
        for (var x = 0; x < ImageSize; x++)
        {
            var pixel = image[x, y];
            inputTensor[0, 0, y, x] = ((pixel.R / 255f) - Mean[0]) / Std[0];
            inputTensor[0, 1, y, x] = ((pixel.G / 255f) - Mean[1]) / Std[1];
            inputTensor[0, 2, y, x] = ((pixel.B / 255f) - Mean[2]) / Std[2];
        }

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("pixel_values", inputTensor) };

        using var results = _session!.Run(inputs);
        var output = results.First().AsTensor<float>();

        var embedding = new float[EmbeddingDimension];
        for (var i = 0; i < EmbeddingDimension; i++)
            embedding[i] = output[0, i];

        var norm = MathF.Sqrt(TensorPrimitives.Dot(embedding, embedding));
        TensorPrimitives.Divide(embedding, norm, embedding);

        return embedding;
    }

    private List<string> ExtractKeyframePaths(string mediaPath, string workDir)
    {
        var keyframesDir = Path.Combine(workDir, "keyframes");
        Directory.CreateDirectory(keyframesDir);

        var outputPattern = Path.Combine(keyframesDir, "keyframe-%03d.png");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        process.StartInfo.ArgumentList.Add("-y");
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(mediaPath);
        process.StartInfo.ArgumentList.Add("-t");
        process.StartInfo.ArgumentList.Add(MotionScanWindowSeconds.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add("-vf");
        process.StartInfo.ArgumentList.Add("select=eq(pict_type\\,I)");
        process.StartInfo.ArgumentList.Add("-vsync");
        process.StartInfo.ArgumentList.Add("vfr");
        process.StartInfo.ArgumentList.Add("-frames:v");
        process.StartInfo.ArgumentList.Add(MotionMaxKeyframes.ToString(CultureInfo.InvariantCulture));
        process.StartInfo.ArgumentList.Add(outputPattern);

        process.Start();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(MotionFfmpegTimeoutMs))
        {
            try
            {
                process.Kill(true);
            }
            catch { }
            _logger.LogWarning("ffmpeg keyframe extraction timed out after {TimeoutMs}ms", MotionFfmpegTimeoutMs);
            return [];
        }

        Task.WaitAll(stdErrTask);
        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "ffmpeg keyframe extraction failed with code {ExitCode}. stderr: {Err}",
                process.ExitCode,
                stdErrTask.Result
            );
            return [];
        }

        return Directory
            .GetFiles(keyframesDir, "keyframe-*.png")
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Take(MotionMaxKeyframes)
            .ToList();
    }

    private void DeleteDirectoryWithRetries(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < 4)
            {
                _logger.LogDebug(ex, "Unable to delete temp directory on attempt {Attempt}, retrying", attempt);
                Thread.Sleep(250);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to delete temp directory {DirectoryPath}", directoryPath);
            }
        }
    }

    private List<float[]> DecodeEmbeddings(DateTime timestamp, params byte[]?[] embeddingsBytes)
    {
        var embeddings = new List<float[]>(embeddingsBytes.Length);
        foreach (var embeddingBytes in embeddingsBytes)
        {
            if (embeddingBytes == null)
                continue;

            var embedding = BytesToFloats(embeddingBytes, timestamp);
            if (embedding.Length != EmbeddingDimension)
            {
                _logger.LogDebug("Skipping candidate with unexpected embedding length {Length}", embedding.Length);
                continue;
            }

            embeddings.Add(embedding);
        }

        return embeddings;
    }

    internal static bool HasTwoDistinctFrameMatches(
        IReadOnlyList<float[]> incomingEmbeddings,
        IReadOnlyList<float[]> candidateEmbeddings,
        float threshold
    )
    {
        if (incomingEmbeddings.Count < 2 || candidateEmbeddings.Count < 2)
            return false;

        var matchedIncoming = new HashSet<int>();
        var matchedCandidate = new HashSet<int>();

        for (var i = 0; i < incomingEmbeddings.Count; i++)
        {
            var incoming = incomingEmbeddings[i];
            var hasMatch = false;
            for (var j = 0; j < candidateEmbeddings.Count; j++)
            {
                var candidate = candidateEmbeddings[j];
                if (incoming.Length != candidate.Length)
                    continue;

                var similarity = TensorPrimitives.CosineSimilarity(incoming, candidate);
                if (similarity < threshold)
                    continue;

                matchedIncoming.Add(i);
                matchedCandidate.Add(j);
                hasMatch = true;
                break;
            }

            if (hasMatch && matchedIncoming.Count >= 2 && matchedCandidate.Count >= 2)
                return true;
        }

        return false;
    }

    private static byte[] FloatsToInt8Bytes(float[] floats)
    {
        var bytes = new byte[floats.Length];
        for (var i = 0; i < floats.Length; i++)
            bytes[i] = (byte)(sbyte)Math.Clamp((int)MathF.Round(floats[i] * 127f), -127, 127);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes, DateTime timestamp)
    {
        if (timestamp < QuantCutoff)
        {
            var floats = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        else
        {
            var floats = new float[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
                floats[i] = (sbyte)bytes[i] / 127f;
            return floats;
        }
    }

    private static string? FindModelPath(string modelFileName)
    {
        var modelPath = Path.Combine("/app/models", modelFileName);
        if (File.Exists(modelPath))
            return modelPath;

        modelPath = Path.Combine(AppContext.BaseDirectory, modelFileName);
        if (File.Exists(modelPath))
            return modelPath;

        if (File.Exists(modelFileName))
            return modelFileName;

        return null;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
