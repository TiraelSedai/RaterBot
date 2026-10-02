using System.Data;
using LinqToDB;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RaterBot.Database;
using Shouldly;
using Telegram.Bot;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RaterBot.Tests.Database;

public class WorkerLifetimeTests : SqliteDbTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessInBackground_ClosesSqliteConnectionAfterCallback(bool telegramFails)
    {
        const long chatId = -1001234567890;
        const int messageId = 42;
        var postId = await InsertPostAsync(chatId, 111, messageId);
        var bot = new Mock<ITelegramBotClient>();
        var databases = new List<SqliteDb>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(bot.Object);
        services.AddSingleton(Mock.Of<IMediaDownloader>());
        services.AddSingleton(Mock.Of<IVectorSearchService>());
        services.AddSingleton<VectorSearchService>();
        services.AddScoped<MessageHandler>();
        services.AddScoped(_ =>
        {
            var db = new SqliteDb(new DataOptions<SqliteDb>(new DataOptions().UseSQLite(Db.ConnectionString!)));
            databases.Add(db);
            return db;
        });
        services.AddSingleton<Worker>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var worker = provider.GetRequiredService<Worker>();
        var me = new User { Id = 999, Username = "testbot" };

        try
        {
            for (var i = 0; i < 3; i++)
            {
                var editStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var editCompleted = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
                bot.Setup(x => x.SendRequest(It.IsAny<EditMessageReplyMarkupRequest>(), It.IsAny<CancellationToken>()))
                    .Returns(() =>
                    {
                        editStarted.TrySetResult();
                        return editCompleted.Task;
                    });
                var update = new Update
                {
                    CallbackQuery = new CallbackQuery
                    {
                        Id = $"vote-{i}",
                        Data = "+",
                        From = new User { Id = 222 + i, FirstName = "Voter" },
                        Message = new Message
                        {
                            Id = messageId,
                            Chat = new Chat { Id = chatId, Type = ChatType.Supergroup },
                        },
                    },
                };

                var processing = worker.ProcessInBackground(me, update);
                try
                {
                    await editStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    databases.Count.ShouldBe(i + 1);
                    var connection = databases[i].TryGetDbConnection().ShouldNotBeNull();
                    connection.State.ShouldBe(ConnectionState.Open);
                    processing.IsCompleted.ShouldBeFalse();

                    if (telegramFails)
                        editCompleted.SetException(new HttpRequestException("Telegram unavailable"));
                    else
                        editCompleted.SetResult(new Message());

                    await processing.WaitAsync(TimeSpan.FromSeconds(5));
                    Db.Interactions.Count(x => x.PostId == postId && x.Reaction).ShouldBe(i + 1);
                    Should.Throw<ObjectDisposedException>(() =>
                    {
                        using var command = connection.CreateCommand();
                    });
                }
                finally
                {
                    editCompleted.TrySetResult(new Message());
                    await processing.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
        }
        finally
        {
            foreach (var db in databases)
                await db.DisposeAsync();
        }
    }
}
