using Shouldly;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using RaterBot.Database;
using RaterBot.Tests.Database;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RaterBot.Tests.Unit;

public class MessageHandlerTests : SqliteDbTestBase
{
    private readonly Mock<ITelegramBotClient> _mockBot = new();
    private readonly Mock<ILogger<MessageHandler>> _mockLogger = new();

    [Fact]
    public void SelectHighestResolutionPhotoFileId_PicksLargestVariant()
    {
        var photos = new[]
        {
            new PhotoSize { FileId = "small", Width = 320, Height = 180 },
            new PhotoSize { FileId = "medium", Width = 640, Height = 360 },
            new PhotoSize { FileId = "large", Width = 1280, Height = 720 },
        };

        var selected = MessageHandler.SelectHighestResolutionPhotoFileId(photos);
        selected.ShouldBe("large");
    }

    private VectorSearchService CreateVectorSearchService()
    {
        var services = new ServiceCollection();
        services.AddLinqToDBContext<SqliteDb>((_, options) => options.UseSQLite("Data Source=:memory:"));
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        return new VectorSearchService(Mock.Of<ILogger<VectorSearchService>>(), _mockBot.Object, scopeFactory);
    }

    private MessageHandler CreateHandler() => new(_mockBot.Object, Db, _mockLogger.Object, CreateVectorSearchService());

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

        _mockBot.Verify(x => x.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("свои посты")),
            It.IsAny<CancellationToken>()), Times.Once);
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

        _mockBot.Verify(x => x.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("уже поставил")),
            It.IsAny<CancellationToken>()), Times.Once);
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

        _mockBot.Verify(x => x.SendRequest(
            It.Is<AnswerCallbackQueryRequest>(r => r.Text != null && r.Text.Contains("не найден")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Update CreateCallbackUpdate(long chatId, long messageId, long userId, string callbackData)
    {
        var chat = new Chat { Id = chatId, Type = ChatType.Supergroup };
        var message = CreateMessage(chat, (int)messageId);
        var from = new User { Id = userId, FirstName = "Test", Username = "testuser" };
        var callbackQuery = new CallbackQuery { Id = "test-callback-id", From = from, Message = message, Data = callbackData };
        return new Update { CallbackQuery = callbackQuery };
    }

    private static Message CreateMessage(Chat chat, int messageId)
    {
        var message = System.Text.Json.JsonSerializer.Deserialize<Message>("{}")!;
        var chatProp = typeof(Message).GetProperty(nameof(Message.Chat))!;
        var idProp = typeof(Message).GetProperty("Id")!;
        chatProp.SetValue(message, chat);
        idProp.SetValue(message, messageId);
        return message;
    }
}
