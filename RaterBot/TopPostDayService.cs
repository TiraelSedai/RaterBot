using LinqToDB;
using RaterBot.Database;
using System.Security.Cryptography.X509Certificates;
using Telegram.Bot;

namespace RaterBot
{
    internal class TopPostDayService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ITelegramBotClient _botClient;
        private readonly ILogger<TopPostDayService> _logger;

        private readonly PeriodicTimer _timer = new(TimeSpan.FromHours(1));

        public TopPostDayService(
            IServiceProvider serviceProvider,
            ITelegramBotClient botClient,
            ILogger<TopPostDayService> logger
        )
        {
            _serviceProvider = serviceProvider;
            _botClient = botClient;
            _logger = logger;
        }

        private async Task MainLoop()
        {
            while (await _timer.WaitForNextTickAsync())
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    using var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();
                    await Tick(db);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TopPostDayService loop");
                }
            }
        }

        private async Task Tick(SqliteDb db)
        {
            var now = DateTime.UtcNow;
            var activeWindow = TimeSpan.FromDays(2);
            var day = TimeSpan.FromDays(1);
            var activeChats = db.Posts
                .Where(x => x.Timestamp > now - activeWindow)
                .Select(x => x.ChatId)
                .Distinct()
                .ToList();

            foreach (var chatId in activeChats)
            {
                var topPosts = db.Posts
                    .Where(x => x.ChatId == chatId && x.Timestamp > now - day)
                    .OrderByDescending(x => x.Interactions.Select(x => x.Reaction ? 1 : -1).Sum())
                    .Take(20)
                    .LoadWith(x => x.Interactions)
                    .ToList();

                var previousTop = db.TopPostsDays.Where(x => x.ChatId == chatId).ToList();

                //var userIdMap = await TelegramHelper.GetTelegramUsers(chat, userIds, _botClient);
            }
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = MainLoop();
            return Task.CompletedTask;
        }
    }
}
