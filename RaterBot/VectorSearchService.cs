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
    private const float EastScoreThreshold = 0.5f;
    private const int OcrMinChars = 20;
    private const int ImageSize = 224;
    private const int EmbeddingDimension = 512;
    private const int OcrTimeoutMs = 10_000;
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

    private record WorkItem(string PhotoFileId, Chat Chat, MessageId MessageId);

    private record ProcessedPost(
        float[] Embedding,
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

    public void Process(string photoFileId, Chat chat, MessageId messageId)
    {
        if (_isEnabled)
            _channel.Writer.TryWrite(new WorkItem(photoFileId, chat, messageId));
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

    private async Task CompareToExistingPosts(WorkItem item, ProcessedPost processed)
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

        await CompareToExistingPostsByClip(item, processed.Embedding);
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

    private async Task CompareToExistingPostsByClip(WorkItem item, float[] embedding)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var now = DateTime.UtcNow;
        var candidates = await db
            .Posts.Where(x =>
                x.ChatId == item.Chat.Id
                && x.MessageId != item.MessageId.Id
                && x.ClipEmbedding != null
                && x.Timestamp > now - _deduplicationWindow
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

    private async Task<ProcessedPost> CalculateAndWriteFeatures(WorkItem item)
    {
        byte[] imageBytes;
        using (var ms = new MemoryStream())
        {
            await _bot.GetInfoAndDownloadFile(item.PhotoFileId, ms);
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
            embedding,
            isTextHeavy,
            ocrTextNormalized,
            ocrConfidence,
            textCoverageRatio,
            shouldUseOcr
        );
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
                new OpenCvSharp.Size(320, 320),
                new Scalar(123.68, 116.78, 103.94),
                swapRB: true,
                crop: false
            );

            _eastTextNet.SetInput(blob);
            using var scores = _eastTextNet.Forward("feature_fusion/Conv_7/Sigmoid");
            var total = checked((int)scores.Total());
            if (total == 0 || scores.Data == IntPtr.Zero)
                return 0f;

            var values = new float[total];
            Marshal.Copy(scores.Data, values, 0, total);

            var textCells = 0;
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i] >= EastScoreThreshold)
                    textCells++;
            }

            return textCells / (float)total;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EAST text detection failed. CLIP-only path will be used for this image.");
            return 0f;
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
