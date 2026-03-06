using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RaterBot.Database;
using RaterBot.Tests.Database;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RaterBot.Tests.Unit;

public class MessageHandlerTests : SqliteDbTestBase
{
    private readonly Mock<ITelegramBotClient> _mockBot = new();
    private readonly Mock<ILogger<MessageHandler>> _mockLogger = new();
    private readonly Mock<IMediaDownloader> _mockDownloader = new();

    [Fact]
    public void SelectHighestResolutionPhotoFileId_PicksLargestVariant()
    {
        var photos = new[]
        {
            new PhotoSize
            {
                FileId = "small",
                Width = 320,
                Height = 180,
            },
            new PhotoSize
            {
                FileId = "medium",
                Width = 640,
                Height = 360,
            },
            new PhotoSize
            {
                FileId = "large",
                Width = 1280,
                Height = 720,
            },
        };

        var selected = MessageHandler.SelectHighestResolutionPhotoFileId(photos);
        selected.ShouldBe("large");
    }

    [Fact]
    public void SelectVectorMedia_PicksPhotoWhenAvailable()
    {
        var photos = new[]
        {
            new PhotoSize
            {
                FileId = "small",
                Width = 320,
                Height = 180,
            },
            new PhotoSize
            {
                FileId = "large",
                Width = 1920,
                Height = 1080,
            },
        };

        var selected = MessageHandler.SelectVectorMedia(
            photos,
            new Video { FileId = "video-id" },
            new Animation { FileId = "animation-id" },
            new Document { FileId = "doc-id", MimeType = "video/mp4" }
        );

        selected.HasValue.ShouldBeTrue();
        selected.Value.FileId.ShouldBe("large");
        selected.Value.MediaKind.ShouldBe(VectorMediaKind.Image);
    }

    [Fact]
    public void SelectVectorMedia_PicksVideo()
    {
        var selected = MessageHandler.SelectVectorMedia(
            photos: null,
            video: new Video { FileId = "video-id" },
            animation: null,
            document: null
        );

        selected.HasValue.ShouldBeTrue();
        selected.Value.FileId.ShouldBe("video-id");
        selected.Value.MediaKind.ShouldBe(VectorMediaKind.Motion);
    }

    [Fact]
    public void SelectVectorMedia_PicksAnimation()
    {
        var selected = MessageHandler.SelectVectorMedia(
            photos: null,
            video: null,
            animation: new Animation { FileId = "animation-id" },
            document: null
        );

        selected.HasValue.ShouldBeTrue();
        selected.Value.FileId.ShouldBe("animation-id");
        selected.Value.MediaKind.ShouldBe(VectorMediaKind.Motion);
    }

    [Fact]
    public void SelectVectorMedia_PicksVideoDocument()
    {
        var selected = MessageHandler.SelectVectorMedia(
            photos: null,
            video: null,
            animation: null,
            document: new Document { FileId = "doc-id", MimeType = "video/webm" }
        );

        selected.HasValue.ShouldBeTrue();
        selected.Value.FileId.ShouldBe("doc-id");
        selected.Value.MediaKind.ShouldBe(VectorMediaKind.Motion);
    }

    [Fact]
    public void SelectVectorMedia_PicksGifDocument()
    {
        var selected = MessageHandler.SelectVectorMedia(
            photos: null,
            video: null,
            animation: null,
            document: new Document { FileId = "gif-id", MimeType = "image/gif" }
        );

        selected.HasValue.ShouldBeTrue();
        selected.Value.FileId.ShouldBe("gif-id");
        selected.Value.MediaKind.ShouldBe(VectorMediaKind.Motion);
    }

    [Fact]
    public void SelectVectorMedia_IgnoresUnsupportedDocument()
    {
        var selected = MessageHandler.SelectVectorMedia(
            photos: null,
            video: null,
            animation: null,
            document: new Document { FileId = "doc-id", MimeType = "image/png" }
        );

        selected.HasValue.ShouldBeFalse();
    }

    private VectorSearchService CreateVectorSearchService()
    {
        var services = new ServiceCollection();
        services.AddLinqToDBContext<SqliteDb>((_, options) => options.UseSQLite("Data Source=:memory:"));
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new VectorSearchService(Mock.Of<ILogger<VectorSearchService>>(), _mockBot.Object, scopeFactory);
    }

    private MessageHandler CreateHandler() =>
        new(_mockBot.Object, Db, _mockLogger.Object, _mockDownloader.Object, CreateVectorSearchService());

    [Fact]
    public async Task HandleCallbackData_PlusVote_CreatesInteraction()
    {
        var posterId = 111L;
        var voterId = 222L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        await InsertPostAsync(chatId, posterId, messageId);

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, voterId, "+");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        var interactions = await Db.Interactions.ToListAsync();
        interactions.Count.ShouldBe(1);
        interactions[0].UserId.ShouldBe(voterId);
        interactions[0].Reaction.ShouldBeTrue();
    }

    [Fact]
    public async Task HandleCallbackData_MinusVote_CreatesInteraction()
    {
        var posterId = 111L;
        var voterId = 222L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        await InsertPostAsync(chatId, posterId, messageId);

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, voterId, "-");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        var interactions = await Db.Interactions.ToListAsync();
        interactions.Count.ShouldBe(1);
        interactions[0].Reaction.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackData_SelfVote_Rejected()
    {
        var posterId = 111L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        await InsertPostAsync(chatId, posterId, messageId);

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, posterId, "+");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        var interactions = await Db.Interactions.ToListAsync();
        interactions.ShouldBeEmpty();

        _mockBot.Verify(
            x =>
                x.SendRequest(
                    It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("свои посты")),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task HandleCallbackData_SameVoteTwice_NoChange()
    {
        var posterId = 111L;
        var voterId = 222L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        var postId = await InsertPostAsync(chatId, posterId, messageId);
        await InsertInteractionAsync(voterId, postId, true);

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, voterId, "+");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        var interactions = await Db.Interactions.ToListAsync();
        interactions.Count.ShouldBe(1);

        _mockBot.Verify(
            x =>
                x.SendRequest(
                    It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("уже поставил")),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task HandleCallbackData_ChangeVote_UpdatesReaction()
    {
        var posterId = 111L;
        var voterId = 222L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        var postId = await InsertPostAsync(chatId, posterId, messageId);
        await InsertInteractionAsync(voterId, postId, true);

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, voterId, "-");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        var interactions = await Db.Interactions.ToListAsync();
        interactions.Count.ShouldBe(1);
        interactions[0].Reaction.ShouldBeFalse();
    }

    [Fact]
    public async Task HandleCallbackData_PostNotFound_AnswersWithError()
    {
        var voterId = 222L;
        var chatId = -1001234567890L;
        var messageId = 42L;

        var handler = CreateHandler();
        var update = CreateCallbackUpdate(chatId, messageId, voterId, "+");
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        _mockBot.Verify(
            x =>
                x.SendRequest(
                    It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("не найден")),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task HandleUpdate_TwitterLink_DownloadSuccess_SendsVideo()
    {
        const long chatId = -1001234567890L;
        const int messageId = 42;
        const int sentVideoMessageId = 600;
        var tempFile = Path.GetTempFileName();
        await File.WriteAllBytesAsync(tempFile, [1, 2, 3]);

        try
        {
            SetupBotResponses(sendMessageMessageId: 500, sendVideoMessageId: sentVideoMessageId);
            _mockDownloader.Setup(x => x.DownloadYtDlp("https://x.com/test/status/123", UrlType.Twitter)).Returns(tempFile);

            var handler = CreateHandler();
            var update = CreateTextUpdate(chatId, messageId, 111L, "https://x.com/test/status/123");
            var botUser = new User { Id = 999, Username = "testbot" };

            await handler.HandleUpdate(botUser, update);

            _mockBot.Verify(x => x.SendRequest(It.IsAny<SendVideoRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            _mockBot.Verify(
                x =>
                    x.SendRequest(
                        It.Is<SendMessageRequest>(r => r.Text != null && r.Text.Contains("fixupx.com")),
                        It.IsAny<CancellationToken>()
                    ),
                Times.Never
            );
            _mockDownloader.Verify(x => x.DownloadYtDlp("https://x.com/test/status/123", UrlType.Twitter), Times.Once);

            var posts = await Db.Posts.ToListAsync();
            posts.Count.ShouldBe(1);
            posts[0].MessageId.ShouldBe(sentVideoMessageId);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task HandleUpdate_TwitterLink_DownloadFailure_FallsBackToFixupx()
    {
        const long chatId = -1001234567890L;
        const int messageId = 42;
        const int sentMessageId = 500;
        const string twitterUrl = "https://x.com/test/status/123?t=abc";
        const string fallbackUrl = "https://fixupx.com/test/status/123?t=abc";

        SetupBotResponses(sendMessageMessageId: sentMessageId, sendVideoMessageId: 600);
        _mockDownloader.Setup(x => x.DownloadYtDlp(twitterUrl, UrlType.Twitter)).Returns((string?)null);

        var handler = CreateHandler();
        var update = CreateTextUpdate(chatId, messageId, 111L, twitterUrl);
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        _mockBot.Verify(x => x.SendRequest(It.IsAny<SendVideoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockBot.Verify(
            x =>
                x.SendRequest(
                    It.Is<SendMessageRequest>(r => r.Text != null && r.Text.Contains(fallbackUrl)),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockDownloader.Verify(x => x.DownloadYtDlp(twitterUrl, UrlType.Twitter), Times.Once);

        var posts = await Db.Posts.ToListAsync();
        posts.Count.ShouldBe(1);
        posts[0].MessageId.ShouldBe(sentMessageId);
    }

    [Fact]
    public async Task HandleUpdate_YoutubeLink_DownloadFailure_FallsBackToOriginalLink()
    {
        const long chatId = -1001234567890L;
        const int messageId = 42;
        const int sentMessageId = 500;
        const string youtubeUrl = "https://www.youtube.com/watch?v=abc123";

        SetupBotResponses(sendMessageMessageId: sentMessageId, sendVideoMessageId: 600);
        _mockDownloader.Setup(x => x.DownloadYtDlp(youtubeUrl, UrlType.Youtube)).Returns((string?)null);

        var handler = CreateHandler();
        var update = CreateTextUpdate(chatId, messageId, 111L, youtubeUrl);
        var botUser = new User { Id = 999, Username = "testbot" };

        await handler.HandleUpdate(botUser, update);

        _mockBot.Verify(x => x.SendRequest(It.IsAny<SendVideoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockBot.Verify(
            x =>
                x.SendRequest(
                    It.Is<SendMessageRequest>(r => r.Text != null && r.Text.Contains(youtubeUrl)),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _mockDownloader.Verify(x => x.DownloadYtDlp(youtubeUrl, UrlType.Youtube), Times.Once);

        var posts = await Db.Posts.ToListAsync();
        posts.Count.ShouldBe(1);
        posts[0].MessageId.ShouldBe(sentMessageId);
    }

    [Fact]
    public void FindSupportedSiteLink_FixupxLink_SkipsDownloaderFallback()
    {
        var message = CreateTextMessage(-1001234567890L, 42, 111L, "https://fixupx.com/test/status/123");

        var result = MessageHandler.FindSupportedSiteLink(message);

        result.HasValue.ShouldBeTrue();
        result.Value.Type.ShouldBe(UrlType.EmbedableLink);
        result.Value.Url.ShouldBe("https://fixupx.com/test/status/123");
        result.Value.FallbackUrl.ShouldBeNull();
    }

    [Fact]
    public void FindSupportedSiteLink_TiktokLink_RemainsYtDlpSource()
    {
        var message = CreateTextMessage(-1001234567890L, 42, 111L, "https://www.tiktok.com/@test/video/123");

        var result = MessageHandler.FindSupportedSiteLink(message);

        result.HasValue.ShouldBeTrue();
        result.Value.Type.ShouldBe(UrlType.TikTok);
        result.Value.Url.ShouldBe("https://www.tiktok.com/@test/video/123");
        result.Value.FallbackUrl.ShouldBeNull();
    }

    [Fact]
    public void FindSupportedSiteLink_YoutubeWatchLink_UsesDownloaderWithOriginalFallback()
    {
        const string youtubeUrl = "https://www.youtube.com/watch?v=abc123";
        var message = CreateTextMessage(-1001234567890L, 42, 111L, youtubeUrl);

        var result = MessageHandler.FindSupportedSiteLink(message);

        result.HasValue.ShouldBeTrue();
        result.Value.Type.ShouldBe(UrlType.Youtube);
        result.Value.Url.ShouldBe(youtubeUrl);
        result.Value.FallbackUrl.ShouldBe(youtubeUrl);
    }

    [Fact]
    public void FindSupportedSiteLink_YoutuBeLink_UsesDownloaderWithOriginalFallback()
    {
        const string youtubeUrl = "https://youtu.be/abc123";
        var message = CreateTextMessage(-1001234567890L, 42, 111L, youtubeUrl);

        var result = MessageHandler.FindSupportedSiteLink(message);

        result.HasValue.ShouldBeTrue();
        result.Value.Type.ShouldBe(UrlType.Youtube);
        result.Value.Url.ShouldBe(youtubeUrl);
        result.Value.FallbackUrl.ShouldBe(youtubeUrl);
    }

    private static Update CreateCallbackUpdate(long chatId, long messageId, long userId, string callbackData)
    {
        var chat = new Chat { Id = chatId, Type = ChatType.Supergroup };
        var message = CreateMessage(chat, (int)messageId);
        var from = new User
        {
            Id = userId,
            FirstName = "Test",
            Username = "testuser",
        };
        var callbackQuery = new CallbackQuery
        {
            Id = "test-callback-id",
            From = from,
            Message = message,
            Data = callbackData,
        };
        return new Update { CallbackQuery = callbackQuery };
    }

    private void SetupBotResponses(int sendMessageMessageId, int sendVideoMessageId)
    {
        _mockBot
            .Setup(x => x.SendRequest(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMessage(new Chat { Id = 1, Type = ChatType.Supergroup }, sendMessageMessageId));
        _mockBot
            .Setup(x => x.SendRequest(It.IsAny<SendVideoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateMessage(new Chat { Id = 1, Type = ChatType.Supergroup }, sendVideoMessageId));
        _mockBot.Setup(x => x.SendRequest(It.IsAny<DeleteMessageRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
    }

    private static Update CreateTextUpdate(long chatId, int messageId, long userId, string text) =>
        new() { Message = CreateTextMessage(chatId, messageId, userId, text) };

    private static Message CreateTextMessage(long chatId, int messageId, long userId, string text)
    {
        var chat = new Chat { Id = chatId, Type = ChatType.Supergroup };
        var message = CreateMessage(chat, messageId);
        SetMessageProperty(message, nameof(Message.Text), text);
        SetMessageProperty(
            message,
            nameof(Message.Entities),
            new[]
            {
                new MessageEntity
                {
                    Type = MessageEntityType.Url,
                    Offset = 0,
                    Length = text.Length,
                },
            }
        );
        SetMessageProperty(
            message,
            nameof(Message.From),
            new User
            {
                Id = userId,
                FirstName = "Test",
                Username = "testuser",
            }
        );
        return message;
    }

    private static Message CreateMessage(Chat chat, int messageId)
    {
        var message = System.Text.Json.JsonSerializer.Deserialize<Message>("{}")!;
        SetMessageProperty(message, nameof(Message.Chat), chat);
        SetMessageProperty(message, "Id", messageId);
        return message;
    }

    private static void SetMessageProperty(Message message, string propertyName, object value) =>
        typeof(Message).GetProperty(propertyName)!.SetValue(message, value);
}
