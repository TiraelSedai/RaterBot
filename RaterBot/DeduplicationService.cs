using System.Text.Json;
using System.Threading.Channels;
using CoenM.ImageHash;
using CoenM.ImageHash.HashAlgorithms;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace RaterBot;

internal sealed class DeduplicationService
{
    private const int SimilarityThreshold = 95;

    private readonly TimeSpan _deduplicationWindow = TimeSpan.FromDays(35);
    private readonly ILogger<DeduplicationService> _logger;
    private readonly ITelegramBotClient _bot;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PerceptualHash _hashAlgo = new();

    record WorkItem(string PhotoFileId, Chat Chat, MessageId MessageId);

    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });

    public DeduplicationService(ILogger<DeduplicationService> logger, ITelegramBotClient bot, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _bot = bot;
        _scopeFactory = scopeFactory;
        Task.Run(WorkLoop);
    }

    public void Process(string photoFileId, Chat chat, MessageId messageId) =>
        _channel.Writer.TryWrite(new WorkItem(photoFileId, chat, messageId));

    private async Task WorkLoop()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                var hash = await CalculateAndWriteHash(item);
                await CompareHashToExistingPosts(item, hash);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to process item from deduplication queue");
            }
        }
    }

    private async Task CompareHashToExistingPosts(WorkItem item, ulong hash)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        var now = DateTime.UtcNow;
        var compareTo = await db
            .Posts.Where(x =>
                x.ChatId == item.Chat.Id
                && x.MessageId != item.MessageId.Id
                && x.MediaHash != null
                && x.Timestamp > now - _deduplicationWindow
            )
            .OrderByDescending(x => x.Timestamp)
            .ToListAsync();

        foreach (var candidate in compareTo)
        {
            var candidateHash = JsonSerializer.Deserialize(candidate.MediaHash!, SourceGenerationContext.Default.ImageHash);
            var similarityPercent = CompareHash.Similarity(hash, candidateHash!.ImgHash);
            if (similarityPercent < SimilarityThreshold)
                continue;
            _logger.LogInformation("Found possible duplicate");
            var linkToMessage = TelegramHelper.LinkToMessage(item.Chat, candidate.MessageId);
            _bot.TemporaryReply(item.Chat.Id, item.MessageId, $"Уже было? {linkToMessage}");
            break;
        }
    }

    private async Task<ulong> CalculateAndWriteHash(WorkItem item)
    {
        _logger.LogDebug(nameof(CalculateAndWriteHash));
        using var ms = new MemoryStream();
        await _bot.GetInfoAndDownloadFile(item.PhotoFileId, ms);
        _logger.LogDebug("Image download ok");
        ms.Seek(0, SeekOrigin.Begin);
        using var image = Image.Load<Rgba32>(ms);
        _logger.LogDebug("Image read ok");
        var hash = _hashAlgo.Hash(image);
        var imageHash = JsonSerializer.Serialize(new ImageHash(hash), SourceGenerationContext.Default.ImageHash);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();

        await db
            .Posts.Where(x => x.ChatId == item.Chat.Id && x.MessageId == item.MessageId.Id)
            .Set(x => x.MediaHash, imageHash)
            .UpdateAsync();
        _logger.LogDebug("New image hash written to database");
        return hash;
    }
}
