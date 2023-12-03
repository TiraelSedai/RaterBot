using LinqToDB;
using RaterBot.Database;
using Telegram.Bot;

namespace RaterBot
{
    /// <summary>
    /// Currently this class does nothing because it's not registered in ConfigureServices. <br/>
    /// The idea was to add #topoftheday tag to messages that are in top-20 for the current day.
    /// It is much easier to scroll through hashtag in Telegram client than to hop between top_posts_day message and each individual message from that top.
    /// However, as of 2023-12-04, it is not possible to update messages that have inline keyboard (source: https://core.telegram.org/bots/api#updating-messages)
    /// "Please note, that it is currently only possible to edit messages without reply_markup or with inline keyboards."
    /// So this whole idea fell apart.
    /// </summary>
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

        //replyMarkup: TelegramHelper.NewPostIkm,
        //            caption: TelegramHelper.MentionUsername(from),
        //            parseMode: ParseMode.MarkdownV2

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

                var previousTopDb = db.Posts
                    .Where(x => x.ChatId == chatId && previousTop.Select(x => x.PostId).Contains(x.MessageId))
                    .ToList();

                var noLongerTop = previousTop.Where(x => !topPosts.Select(x => x.MessageId).Contains(x.PostId));

                foreach (var post in noLongerTop)
                {
                    var prevTopDb = previousTopDb.SingleOrDefault(x => x.MessageId == post.PostId);
                    var caption = prevTopDb?.ReplyMessageId == null;

                    if (caption)
                    {
                        // Do
                    }
                    db.TopPostsDays.Delete(x => x.Id == post.Id);
                }
            }
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = MainLoop();
            return Task.CompletedTask;
        }
    }
}
