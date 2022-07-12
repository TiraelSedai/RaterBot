using LinqToDB.Data;
using RaterBot.Database;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RaterBot
{
    internal sealed class Worker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<MessageHandler> _logger;

        public Worker(IServiceProvider serviceProvider, ITelegramBotClient botClient, ILogger<MessageHandler> logger)
        {
            _serviceProvider = serviceProvider;
            _botClient = botClient;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var me = await _botClient.GetMeAsync(cancellationToken: stoppingToken);
            await _botClient.SetMyCommandsAsync(
                new[]
                {
                    new BotCommand
                    {
                        Command = "text",
                        Description = "Реплай на текстовое сообщение/линк чтобы бот преобразовал его в оцениваемое"
                    },
                    new BotCommand { Command = "top_posts_day", Description = "Топ постов дня" },
                    new BotCommand { Command = "top_posts_week", Description = "Топ постов недели" },
                    new BotCommand { Command = "top_authors_week", Description = "Топ авторов недели" },
                    new BotCommand { Command = "top_authors_month", Description = "Топ авторов месяца" },
                    new BotCommand { Command = "controversial_week", Description = "Топ противоречивых недели" },
                    new BotCommand { Command = "controversial_month", Description = "Топ противоречивых месяца" },
                    new BotCommand
                    {
                        Command = "delete",
                        Description = "Реплай на своё случайно преобразованное сообщение чтобы удалить его"
                    },
                    new BotCommand
                    {
                        Command = "ignore",
                        Description =
                            "или #ignore, или /skip, или #skip - добавь к видео или фото, чтобы бот не преобразовывал его"
                    }
                },
                cancellationToken: stoppingToken
            );

            var offset = 0;
            while (!stoppingToken.IsCancellationRequested)
                try
                {
                    string? mediaGroupId = null;
                    var updates = await _botClient.GetUpdatesAsync(
                        offset,
                        100,
                        1800,
                        allowedUpdates: new[] { UpdateType.CallbackQuery, UpdateType.Message },
                        cancellationToken: stoppingToken
                    );
                    if (!updates.Any())
                    {
                        using var scope = _serviceProvider.CreateScope();
                        using var dbc = scope.ServiceProvider.GetRequiredService<SqliteDb>();
                        dbc.Execute("PRAGMA optimize;");
                        continue;
                    }
                    var exceptIgnored = updates.Where(x => !ShouldBeIgnored(x));

                    foreach (var update in exceptIgnored)
                    {
                        if (update.Type == UpdateType.Message)
                        {
                            if (update.Message!.MediaGroupId != null && update.Message!.MediaGroupId == mediaGroupId)
                                continue;
                            mediaGroupId = update.Message.MediaGroupId;
                        }
                        offset = update.Id + 1;
                        _ = ProcessInBackground(me, update);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "General update exception");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
        }

        private async Task ProcessInBackground(User me, Update update)
        {
            var scope = _serviceProvider.CreateScope();
            var mh = scope.ServiceProvider.GetRequiredService<MessageHandler>();
            await mh.HandleUpdate(me, update);
        }

        private static bool ShouldBeIgnored(Update update)
        {
            if (update.Type != UpdateType.Message)
                return false;

            var caption = update.Message?.Caption?.ToLower();
            return !string.IsNullOrWhiteSpace(caption)
                && (
                    caption.Contains("/skip")
                    || caption.Contains("/ignore")
                    || caption.Contains("#skip")
                    || caption.Contains("#ignore")
                );
        }
    }
}
