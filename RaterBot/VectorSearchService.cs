using System.Diagnostics;
using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using LinqToDB;
using LinqToDB.Async;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using RaterBot.Database;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace RaterBot;

internal sealed partial class VectorSearchService : IDisposable
{
    private const float SimilarityThreshold = 0.96f;
    private const float TextCoverageThreshold = 0.25f;
    private const float OcrSimilarityThreshold = 0.80f;
    private const float OcrMinConfidence = 55f;
    private const float EastScoreThreshold = 0.35f;
    private const float EastNmsThreshold = 0.30f;
    private const int EastInputSize = 320;
    private const int EastStride = 4;
    private const int OcrMinChars = 20;
    private const int ImageSize = 224;
    private const int EmbeddingDimension = 512;
    private const int OcrTimeoutMs = 10_000;
    private const int MotionScanWindowSeconds = 15;
    private const int MotionMaxKeyframes = 5;
    private const int MotionFfmpegTimeoutMs = 30_000;
    private const string OcrLanguages = "eng+rus";

    private static readonly DateTime QuantCutoff = new(2026, 1, 29, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Regex MultiWhitespaceRegex = MultiWhitespace();

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromDays(30);
    private readonly ILogger<VectorSearchService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InferenceSession? _session;
    private readonly Net? _eastTextNet;
    private readonly bool _isEnabled;
    private bool _missingTesseractLogged;

    private record WorkItem(string FileId, VectorMediaKind MediaKind, Chat Chat, MessageId MessageId);

    private record ProcessedPost(
        VectorMediaKind MediaKind,
        IReadOnlyList<float[]> Embeddings,
        bool IsTextHeavy,
        string? OcrTextNormalized,
        float? OcrAvgConfidence,
        float TextCoverageRatio,
        bool ShouldUseOcr
    );

    private record OcrResult(string RawText, float AvgConfidence, bool Success)
    {
        public static OcrResult Empty => new("", 0f, false);

        public float QualityScore =>
            (Success ? Math.Max(AvgConfidence, 0f) : 0f) + Math.Min(RawText.Length, 200) / 4f;
    }

    internal readonly record struct EastCandidate(Rect2f Box, float Confidence);

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

        var eastPath = FindModelPath("frozen_east_text_detection.pb");
        if (eastPath != null)
        {
            try
            {
                _eastTextNet = CvDnn.ReadNetFromTensorflow(eastPath);
                _logger.LogInformation("EAST text detector loaded from {ModelPath}", eastPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to load EAST model from {ModelPath}. CLIP-only fallback will be used.", eastPath);
            }
        }
        else
        {
            _logger.LogWarning("EAST model not found, OCR routing disabled. Expected frozen_east_text_detection.pb");
        }

        _logger.LogDebug("VectorSearchService ctor end");
    }

    public void Process(string fileId, VectorMediaKind mediaKind, Chat chat, MessageId messageId)
    {
        if (_isEnabled)
            _channel.Writer.TryWrite(new WorkItem(fileId, mediaKind, chat, messageId));
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

    private async Task<ProcessedPost?> CalculateAndWriteFeatures(WorkItem item)
    {
        return item.MediaKind switch
        {
            VectorMediaKind.Image => await CalculateAndWriteImageFeatures(item),
            VectorMediaKind.Motion => await CalculateAndWriteMotionFeatures(item),
            _ => null,
        };
    }

    private async Task CompareToExistingPosts(WorkItem item, ProcessedPost? processed)
    {
        if (processed == null)
            return;

        if (item.MediaKind == VectorMediaKind.Motion)
        {
            await CompareToExistingMotionPostsByClip(item, processed.Embeddings);
            return;
        }

        await CompareToExistingImagePosts(item, processed);
    }

    private async Task CompareToExistingImagePosts(WorkItem item, ProcessedPost processed)
    {
        if (processed.ShouldUseOcr && !string.IsNullOrWhiteSpace(processed.OcrTextNormalized))
        {
            var foundByOcr = await CompareToExistingPostsByOcr(item, processed.OcrTextNormalized);
            if (foundByOcr)
                return;

            _logger.LogDebug("OCR route selected, no duplicate found");
            return;
        }

        if (processed.IsTextHeavy)
            _logger.LogDebug("Using CLIP fallback for text-heavy image because OCR quality was insufficient");

        await CompareToExistingImagePostsByClip(item, processed.Embeddings[0]);
    }

    private async Task<bool> CompareToExistingPostsByOcr(WorkItem item, string normalizedText)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var now = DateTime.UtcNow;
        var candidates = await db
            .Posts.Where(x =>
                x.ChatId == item.Chat.Id
                && x.MessageId != item.MessageId.Id
                && x.IsTextHeavy == true
                && x.OcrTextNormalized != null
                && x.Timestamp > now - _deduplicationWindow
            )
            .OrderByDescending(x => x.Timestamp)
            .Select(x => new
            {
                x.MessageId,
                x.OcrTextNormalized,
            })
            .ToListAsync();

        _logger.LogDebug("Found {Count} OCR duplicate candidates", candidates.Count);
        foreach (var candidate in candidates)
        {
            var similarity = TokenDiceSimilarity(normalizedText, candidate.OcrTextNormalized ?? "");
            if (similarity < OcrSimilarityThreshold)
                continue;

            _logger.LogInformation("Found possible duplicate via OCR (similarity: {Similarity:F3})", similarity);
            var linkToMessage = TelegramHelper.LinkToMessage(item.Chat, candidate.MessageId);
            _bot.TemporaryReply(item.Chat.Id, item.MessageId, $"Уже было? {linkToMessage}");
            return true;
        }

        return false;
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

    private async Task<ProcessedPost> CalculateAndWriteImageFeatures(WorkItem item)
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

        var textCoverageRatio = DetectTextCoverageRatio(imageBytes);
        var isTextHeavy = textCoverageRatio >= TextCoverageThreshold;
        var ocr = isTextHeavy ? ExtractBestOcr(imageBytes) : OcrResult.Empty;
        var ocrTextNormalized = ocr.Success ? NormalizeOcrText(ocr.RawText) : null;
        var ocrConfidence = ocr.Success ? ocr.AvgConfidence : (float?)null;
        var shouldUseOcr = isTextHeavy && ShouldUseOcrRoute(textCoverageRatio, ocrTextNormalized, ocrConfidence);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var coverageDb = (double?)textCoverageRatio;
        var ocrConfidenceDb = (double?)ocrConfidence;
        await db
            .Posts.Where(x => x.ChatId == item.Chat.Id && x.MessageId == item.MessageId.Id)
            .Set(x => x.ClipEmbedding, embeddingBytes)
            .Set(x => x.ClipEmbedding2, (byte[]?)null)
            .Set(x => x.ClipEmbedding3, (byte[]?)null)
            .Set(x => x.ClipEmbedding4, (byte[]?)null)
            .Set(x => x.ClipEmbedding5, (byte[]?)null)
            .Set(x => x.VectorMediaKind, (int)VectorMediaKind.Image)
            .Set(x => x.TextCoverageRatio, coverageDb)
            .Set(x => x.IsTextHeavy, isTextHeavy)
            .Set(x => x.OcrTextNormalized, ocrTextNormalized)
            .Set(x => x.OcrAvgConfidence, ocrConfidenceDb)
            .UpdateAsync();

        _logger.LogInformation(
            "Image analyzed. Coverage={Coverage:P1}, TextHeavy={TextHeavy}, OcrChars={Chars}, OcrConfidence={Confidence:F1}, Route={Route}",
            textCoverageRatio,
            isTextHeavy,
            ocrTextNormalized?.Length ?? 0,
            ocrConfidence ?? 0f,
            shouldUseOcr ? "OCR" : isTextHeavy ? "CLIP_FALLBACK" : "CLIP"
        );

        return new ProcessedPost(
            VectorMediaKind.Image,
            [embedding],
            isTextHeavy,
            ocrTextNormalized,
            ocrConfidence,
            textCoverageRatio,
            shouldUseOcr
        );
    }

    private async Task<ProcessedPost?> CalculateAndWriteMotionFeatures(WorkItem item)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"raterbot-motion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var mediaPath = Path.Combine(tempDir, "input_media.bin");
            await using (var stream = File.Create(mediaPath))
                await _bot.GetInfoAndDownloadFile(item.FileId, stream);

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
                .Set(x => x.TextCoverageRatio, (double?)null)
                .Set(x => x.IsTextHeavy, (bool?)null)
                .Set(x => x.OcrTextNormalized, (string?)null)
                .Set(x => x.OcrAvgConfidence, (double?)null)
                .UpdateAsync();

            _logger.LogInformation("Motion media analyzed. Keyframes={Count}", embeddings.Count);

            return new ProcessedPost(
                VectorMediaKind.Motion,
                embeddings,
                IsTextHeavy: false,
                OcrTextNormalized: null,
                OcrAvgConfidence: null,
                TextCoverageRatio: 0f,
                ShouldUseOcr: false
            );
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
            catch
            {
            }
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

        return Directory.GetFiles(keyframesDir, "keyframe-*.png")
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

    private float DetectTextCoverageRatio(byte[] imageBytes)
    {
        if (_eastTextNet == null)
            return 0f;

        try
        {
            using var src = Cv2.ImDecode(imageBytes, ImreadModes.Color);
            if (src.Empty())
                return 0f;

            using var blob = CvDnn.BlobFromImage(
                src,
                1.0,
                new OpenCvSharp.Size(EastInputSize, EastInputSize),
                new Scalar(123.68, 116.78, 103.94),
                swapRB: true,
                crop: false
            );

            _eastTextNet.SetInput(blob);
            using var scores = _eastTextNet.Forward("feature_fusion/Conv_7/Sigmoid");
            using var geometry = _eastTextNet.Forward("feature_fusion/concat_3");
            var scoreTotal = checked((int)scores.Total());
            var geometryTotal = checked((int)geometry.Total());
            if (scoreTotal == 0 || geometryTotal == 0 || scores.Data == IntPtr.Zero || geometry.Data == IntPtr.Zero)
                return 0f;

            var scoresData = new float[scoreTotal];
            var geometryData = new float[geometryTotal];
            Marshal.Copy(scores.Data, scoresData, 0, scoreTotal);
            Marshal.Copy(geometry.Data, geometryData, 0, geometryTotal);

            var featureMapSize = EastInputSize / EastStride;
            var expectedScoreCount = featureMapSize * featureMapSize;
            var expectedGeometryCount = expectedScoreCount * 5;
            if (scoresData.Length != expectedScoreCount || geometryData.Length != expectedGeometryCount)
            {
                _logger.LogWarning(
                    "Unexpected EAST output dimensions. Scores={ScoresCount} Geometry={GeometryCount} ExpectedScores={ExpectedScores} ExpectedGeometry={ExpectedGeometry}",
                    scoresData.Length,
                    geometryData.Length,
                    expectedScoreCount,
                    expectedGeometryCount
                );
                return 0f;
            }

            var candidates = DecodeEastCandidates(
                scoresData,
                geometryData,
                featureMapSize,
                featureMapSize,
                src.Width,
                src.Height,
                EastScoreThreshold
            );
            if (candidates.Count == 0)
            {
                _logger.LogDebug("EAST produced no candidates above confidence threshold {Threshold}", EastScoreThreshold);
                return 0f;
            }

            var nmsBoxes = ApplyNms(candidates, EastNmsThreshold);
            var coverage = ComputeUnionCoverageRatio(nmsBoxes, src.Width, src.Height);
            _logger.LogDebug(
                "EAST coverage computed from box union. RawBoxes={RawCount}, NmsBoxes={NmsCount}, Coverage={Coverage:P1}",
                candidates.Count,
                nmsBoxes.Count,
                coverage
            );
            return coverage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EAST text detection failed. CLIP-only path will be used for this image.");
            return 0f;
        }
    }

    internal static List<EastCandidate> DecodeEastCandidates(
        IReadOnlyList<float> scores,
        IReadOnlyList<float> geometry,
        int featureMapHeight,
        int featureMapWidth,
        int imageWidth,
        int imageHeight,
        float scoreThreshold
    )
    {
        var cellCount = featureMapHeight * featureMapWidth;
        if (featureMapHeight <= 0 || featureMapWidth <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return [];
        if (scores.Count != cellCount || geometry.Count != cellCount * 5)
            return [];

        var scaleX = imageWidth / (float)EastInputSize;
        var scaleY = imageHeight / (float)EastInputSize;
        var result = new List<EastCandidate>(Math.Min(512, cellCount));

        for (var y = 0; y < featureMapHeight; y++)
            for (var x = 0; x < featureMapWidth; x++)
            {
                var offset = y * featureMapWidth + x;
                var confidence = scores[offset];
                if (confidence < scoreThreshold)
                    continue;

                var top = geometry[offset];
                var right = geometry[cellCount + offset];
                var bottom = geometry[cellCount * 2 + offset];
                var left = geometry[cellCount * 3 + offset];
                var angle = geometry[cellCount * 4 + offset];

                var cos = MathF.Cos(angle);
                var sin = MathF.Sin(angle);
                var boxHeight = top + bottom;
                var boxWidth = right + left;
                if (boxWidth <= 0f || boxHeight <= 0f)
                    continue;

                var offsetX = x * EastStride;
                var offsetY = y * EastStride;
                var endX = offsetX + cos * right + sin * bottom;
                var endY = offsetY - sin * right + cos * bottom;
                var startX = endX - boxWidth;
                var startY = endY - boxHeight;

                var x1 = Math.Clamp(MathF.Min(startX, endX) * scaleX, 0f, imageWidth);
                var y1 = Math.Clamp(MathF.Min(startY, endY) * scaleY, 0f, imageHeight);
                var x2 = Math.Clamp(MathF.Max(startX, endX) * scaleX, 0f, imageWidth);
                var y2 = Math.Clamp(MathF.Max(startY, endY) * scaleY, 0f, imageHeight);
                if (x2 <= x1 || y2 <= y1)
                    continue;

                result.Add(new EastCandidate(new Rect2f(x1, y1, x2 - x1, y2 - y1), confidence));
            }

        return result;
    }

    internal static List<Rect2f> ApplyNms(IReadOnlyList<EastCandidate> candidates, float iouThreshold)
    {
        if (candidates.Count == 0)
            return [];

        var sortedIndexes = Enumerable.Range(0, candidates.Count)
            .OrderByDescending(i => candidates[i].Confidence)
            .ToArray();
        var selected = new List<Rect2f>(candidates.Count);

        foreach (var idx in sortedIndexes)
        {
            var candidate = candidates[idx].Box;
            var shouldKeep = true;
            for (var i = 0; i < selected.Count; i++)
            {
                if (ComputeIntersectionOverUnion(candidate, selected[i]) > iouThreshold)
                {
                    shouldKeep = false;
                    break;
                }
            }

            if (shouldKeep)
                selected.Add(candidate);
        }

        return selected;
    }

    internal static float ComputeUnionCoverageRatio(IReadOnlyList<Rect2f> boxes, int imageWidth, int imageHeight)
    {
        if (boxes.Count == 0 || imageWidth <= 0 || imageHeight <= 0)
            return 0f;

        var events = new List<(float X, bool IsStart, float Y1, float Y2)>(boxes.Count * 2);
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var x1 = Math.Clamp(box.X, 0f, imageWidth);
            var y1 = Math.Clamp(box.Y, 0f, imageHeight);
            var x2 = Math.Clamp(box.X + box.Width, 0f, imageWidth);
            var y2 = Math.Clamp(box.Y + box.Height, 0f, imageHeight);
            if (x2 <= x1 || y2 <= y1)
                continue;

            events.Add((x1, true, y1, y2));
            events.Add((x2, false, y1, y2));
        }

        if (events.Count == 0)
            return 0f;

        events.Sort((left, right) => left.X.CompareTo(right.X));

        var active = new List<(float Y1, float Y2)>();
        var previousX = events[0].X;
        double area = 0d;

        for (var i = 0; i < events.Count; i++)
        {
            var current = events[i];
            var width = current.X - previousX;
            if (width > 0f && active.Count > 0)
            {
                var height = ComputeMergedIntervalLength(active);
                area += width * height;
            }

            if (current.IsStart)
            {
                active.Add((current.Y1, current.Y2));
            }
            else
            {
                RemoveFirstMatchingInterval(active, current.Y1, current.Y2);
            }

            previousX = current.X;
        }

        var totalArea = (double)imageWidth * imageHeight;
        if (totalArea <= 0d)
            return 0f;

        return Math.Clamp((float)(area / totalArea), 0f, 1f);
    }

    private static float ComputeIntersectionOverUnion(Rect2f left, Rect2f right)
    {
        var interLeft = MathF.Max(left.X, right.X);
        var interTop = MathF.Max(left.Y, right.Y);
        var interRight = MathF.Min(left.X + left.Width, right.X + right.Width);
        var interBottom = MathF.Min(left.Y + left.Height, right.Y + right.Height);
        var interWidth = interRight - interLeft;
        var interHeight = interBottom - interTop;
        if (interWidth <= 0f || interHeight <= 0f)
            return 0f;

        var intersection = interWidth * interHeight;
        var leftArea = left.Width * left.Height;
        var rightArea = right.Width * right.Height;
        var union = leftArea + rightArea - intersection;
        if (union <= 0f)
            return 0f;

        return intersection / union;
    }

    private static float ComputeMergedIntervalLength(IReadOnlyList<(float Y1, float Y2)> intervals)
    {
        if (intervals.Count == 0)
            return 0f;

        var sorted = intervals.OrderBy(x => x.Y1).ToArray();
        var length = 0f;
        var currentStart = sorted[0].Y1;
        var currentEnd = sorted[0].Y2;

        for (var i = 1; i < sorted.Length; i++)
        {
            var (start, end) = sorted[i];
            if (start <= currentEnd)
            {
                currentEnd = MathF.Max(currentEnd, end);
                continue;
            }

            length += MathF.Max(0f, currentEnd - currentStart);
            currentStart = start;
            currentEnd = end;
        }

        length += MathF.Max(0f, currentEnd - currentStart);
        return length;
    }

    private static void RemoveFirstMatchingInterval(List<(float Y1, float Y2)> intervals, float y1, float y2)
    {
        const float epsilon = 0.0001f;
        for (var i = 0; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            if (MathF.Abs(interval.Y1 - y1) <= epsilon && MathF.Abs(interval.Y2 - y2) <= epsilon)
            {
                intervals.RemoveAt(i);
                return;
            }
        }
    }

    private OcrResult ExtractBestOcr(byte[] imageBytes)
    {
        var normal = RunTesseract(imageBytes, invert: false);
        var inverted = RunTesseract(imageBytes, invert: true);
        return normal.QualityScore >= inverted.QualityScore ? normal : inverted;
    }

    private OcrResult RunTesseract(byte[] imageBytes, bool invert)
    {
        var tempFilePath = Path.Combine(Path.GetTempPath(), $"raterbot-ocr-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = Image.Load<Rgb24>(imageBytes))
            {
                var targetSize = new SixLabors.ImageSharp.Size(Math.Max(1, image.Width * 2), Math.Max(1, image.Height * 2));
                image.Mutate(x =>
                {
                    x.Grayscale();
                    if (invert)
                        x.Invert();
                    x.Resize(new ResizeOptions { Size = targetSize, Mode = ResizeMode.Stretch });
                });
                image.Save(tempFilePath);
            }

            using var process = System.Diagnostics.Process.Start(
                new ProcessStartInfo
                {
                    FileName = "tesseract",
                    Arguments = $"\"{tempFilePath}\" stdout -l {OcrLanguages} --psm 6 tsv",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );

            if (process == null)
                return OcrResult.Empty;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(OcrTimeoutMs))
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }
                _logger.LogWarning("Tesseract timed out after {TimeoutMs}ms", OcrTimeoutMs);
                return OcrResult.Empty;
            }

            Task.WaitAll(outputTask, errorTask);
            if (process.ExitCode != 0)
            {
                _logger.LogDebug("Tesseract exited with code {ExitCode}. stderr: {Err}", process.ExitCode, errorTask.Result);
                return OcrResult.Empty;
            }

            return ParseTesseractTsv(outputTask.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (!_missingTesseractLogged)
            {
                _missingTesseractLogged = true;
                _logger.LogWarning(ex, "Tesseract binary not found. OCR routing disabled until runtime dependency is installed.");
            }
            return OcrResult.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tesseract OCR failed");
            return OcrResult.Empty;
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                }
            }
        }
    }

    private static OcrResult ParseTesseractTsv(string tsv)
    {
        if (string.IsNullOrWhiteSpace(tsv))
            return OcrResult.Empty;

        var words = new List<string>(128);
        var confidenceSum = 0f;
        var confidenceCount = 0;
        var lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split('\t');
            if (parts.Length < 12)
                continue;
            if (!float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
                continue;
            if (confidence < 0f)
                continue;

            var word = string.Join('\t', parts.Skip(11)).Trim();
            if (string.IsNullOrWhiteSpace(word))
                continue;

            words.Add(word);
            confidenceSum += confidence;
            confidenceCount++;
        }

        if (words.Count == 0 || confidenceCount == 0)
            return OcrResult.Empty;

        return new OcrResult(string.Join(' ', words), confidenceSum / confidenceCount, true);
    }

    internal static bool ShouldUseOcrRoute(float textCoverageRatio, string? normalizedText, float? ocrAvgConfidence)
    {
        if (textCoverageRatio < TextCoverageThreshold)
            return false;
        if (string.IsNullOrWhiteSpace(normalizedText))
            return false;
        if (normalizedText.Length < OcrMinChars)
            return false;
        if (!ocrAvgConfidence.HasValue || ocrAvgConfidence.Value < OcrMinConfidence)
            return false;

        return true;
    }

    internal static string NormalizeOcrText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                sb.Append(c);
            else
                sb.Append(' ');
        }

        return MultiWhitespaceRegex.Replace(sb.ToString().Trim(), " ");
    }

    internal static float TokenDiceSimilarity(string left, string right)
    {
        var leftNormalized = NormalizeOcrText(left);
        var rightNormalized = NormalizeOcrText(right);

        if (leftNormalized.Length == 0 || rightNormalized.Length == 0)
            return 0f;

        var leftTokens = leftNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = rightNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
            return 0f;

        var intersection = leftTokens.Intersect(rightTokens).Count();
        return (2f * intersection) / (leftTokens.Count + rightTokens.Count);
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
        _eastTextNet?.Dispose();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespace();
}
