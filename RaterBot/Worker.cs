using System.Text.RegularExpressions;
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
        private readonly VectorSearchService _vectorSearchService;

        public Worker(
            IServiceProvider serviceProvider,
            ITelegramBotClient botClient,
            ILogger<MessageHandler> logger,
            VectorSearchService vectorSearchService
        )
        {
            _serviceProvider = serviceProvider;
            _botClient = botClient;
            _logger = logger;
            _vectorSearchService = vectorSearchService;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "VectorSearchService eager init complete: {ServiceType}",
                _vectorSearchService.GetType().Name
            );

            var me = await _botClient.GetMe(cancellationToken: stoppingToken);
            await _botClient.SetMyCommands(
                [
                    new BotCommand
                    {
                        Command = "text",
                        Description = "Реплай на текстовое сообщение/линк чтобы бот преобразовал его в оцениваемое",
                    },
                    new BotCommand { Command = "top_posts_day", Description = "Топ постов дня" },
                    new BotCommand { Command = "top_posts_week", Description = "Топ постов недели" },
                    new BotCommand { Command = "top_posts_month", Description = "Топ постов месяца" },
                    new BotCommand { Command = "top_authors_week", Description = "Топ авторов недели" },
                    new BotCommand { Command = "top_authors_month", Description = "Топ авторов месяца" },
                    new BotCommand { Command = "controversial_week", Description = "Топ противоречивых недели" },
                    new BotCommand { Command = "controversial_month", Description = "Топ противоречивых месяца" },
                    new BotCommand
                    {
                        Command = "delete",
                        Description = "Реплай на своё случайно преобразованное сообщение чтобы удалить его",
                    },
                    new BotCommand
                    {
                        Command = "ignore",
                        Description = "или #ignore, или /skip, или #skip - добавь к видео или фото, чтобы бот не преобразовывал его",
                    },
                ],
                cancellationToken: stoppingToken
            );

            string? mediaGroupId = null;
            var offset = 0;
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await _botClient.GetUpdates(
                        offset,
                        100,
                        300,
                        allowedUpdates: [UpdateType.CallbackQuery, UpdateType.Message],
                        cancellationToken: stoppingToken
                    );
                    if (updates.Length == 0)
                    {
                        using var scope = _serviceProvider.CreateScope();
                        using var dbc = scope.ServiceProvider.GetRequiredService<SqliteDb>();
                        dbc.Execute("PRAGMA optimize;");
                        continue;
                    }

                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;
                        if (update.Type == UpdateType.Message)
                        {
                            if (update.Message!.MediaGroupId != null && update.Message!.MediaGroupId == mediaGroupId)
                                continue;
                            mediaGroupId = update.Message.MediaGroupId;
                            if (ShouldBeIgnored(update.Message))
                                continue;
                        }

                        _ = ProcessInBackground(me, update);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "General update exception");
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                }
            }
        }

        private async Task ProcessInBackground(User me, Update update)
        {
            var scope = _serviceProvider.CreateScope();
            var mh = scope.ServiceProvider.GetRequiredService<MessageHandler>();
            await mh.HandleUpdate(me, update);
        }

        private static bool ShouldBeIgnored(Message message)
        {
            var text = message.Caption ?? message.Text;
            return text != null
                && Regex.IsMatch(text, "(\\/|#)(ignore|skip)", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }
    }
}
