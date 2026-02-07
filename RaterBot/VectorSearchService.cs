using System.Numerics.Tensors;
using System.Threading.Channels;
using LinqToDB;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RaterBot.Database;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace RaterBot;

internal sealed class VectorSearchService : IDisposable
{
    private const float SimilarityThreshold = 0.96f;
    private const int ImageSize = 224;
    private const int EmbeddingDimension = 512;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromDays(30);
    private readonly ILogger<VectorSearchService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InferenceSession? _session;
    private readonly bool _isEnabled;

    private record WorkItem(string PhotoFileId, Chat Chat, MessageId MessageId);

    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public VectorSearchService(ILogger<VectorSearchService> logger, ITelegramBotClient bot, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _bot = bot;
        _scopeFactory = scopeFactory;

        var modelPath = "/app/models/vision_model_quantized.onnx";
        if (!File.Exists(modelPath))
            modelPath = Path.Combine(AppContext.BaseDirectory, "vision_model_quantized.onnx");
        if (!File.Exists(modelPath))
            modelPath = "vision_model_quantized.onnx";

        if (File.Exists(modelPath))
        {
            _session = new InferenceSession(modelPath);
            _isEnabled = true;
            _logger.LogInformation("VectorSearchService enabled, model loaded from {ModelPath}", modelPath);
            Task.Run(WorkLoop);
        }
        else
        {
            _isEnabled = false;
            _logger.LogWarning("VectorSearchService disabled: vision_model_quantized.onnx not found");
        }
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
                var embedding = await CalculateAndWriteEmbedding(item);
                await CompareToExistingPosts(item, embedding);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to process item from vector search queue");
            }
        }
    }

    private async Task CompareToExistingPosts(WorkItem item, float[] embedding)
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
            .Select(x => new { x.MessageId, x.ClipEmbedding, x.Timestamp })
            .ToListAsync();
        _logger.LogDebug("Found {Count} duplicate candidates", candidates.Count);

        foreach (var candidate in candidates)
        {
            var candidateEmbedding = BytesToFloats(candidate.ClipEmbedding!);
            if (embedding.Length != candidateEmbedding.Length)
            {
                _logger.LogDebug("Different length vectors. target {EmbeddingLen} candidate {CandidateLne}", embedding.Length, candidateEmbedding.Length);
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

    private async Task<float[]> CalculateAndWriteEmbedding(WorkItem item)
    {
        using var ms = new MemoryStream();
        await _bot.GetInfoAndDownloadFile(item.PhotoFileId, ms);

        ms.Seek(0, SeekOrigin.Begin);
        using var image = Image.Load<Rgb24>(ms);

        var embedding = GetEmbedding(image);
        var embeddingBytes = FloatsToInt8Bytes(embedding);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        await db
            .Posts.Where(x => x.ChatId == item.Chat.Id && x.MessageId == item.MessageId.Id)
            .Set(x => x.ClipEmbedding, embeddingBytes)
            .UpdateAsync();

        _logger.LogDebug("New CLIP int8 embedding written to database");
        return embedding;
    }

    private float[] GetEmbedding(Image<Rgb24> image)
    {
        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(ImageSize, ImageSize), Mode = ResizeMode.Crop }));

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

    private static byte[] FloatsToInt8Bytes(float[] floats)
    {
        var bytes = new byte[floats.Length];
        for (var i = 0; i < floats.Length; i++)
            bytes[i] = (byte)(sbyte)Math.Clamp((int)MathF.Round(floats[i] * 127f), -127, 127);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
            floats[i] = (sbyte)bytes[i] / 127f;
        return floats;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }
}
