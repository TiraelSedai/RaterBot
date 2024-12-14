using LinqToDB;
using RaterBot.Database;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace RaterBot
{
    internal class TopPostDayService(
        IServiceProvider serviceProvider,
        ITelegramBotClient botClient,
        Polly polly,
        ILogger<TopPostDayService> logger
    ) : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider = serviceProvider;
        private readonly ITelegramBotClient _botClient = botClient;
        private readonly Polly _polly = polly;
        private readonly ILogger<TopPostDayService> _logger = logger;

        private readonly PeriodicTimer _timer = new(TimeSpan.FromMinutes(60));

        private async Task MainLoop()
        {
            while (await _timer.WaitForNextTickAsync())
            {
                try
                {
                    _logger.LogDebug("TopPostDayService tick");
                    using var scope = _serviceProvider.CreateScope();
                    await using var db = scope.ServiceProvider.GetRequiredService<SqliteDb>();
                    await Tick(db);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TopPostDayService loop");
                }
            }
        }

        private const string TopOfTheDayHashTag = "#TopOfTheDay";

        private async Task Tick(SqliteDb db)
        {
            var now = DateTime.UtcNow;
            var activeWindow = TimeSpan.FromDays(2);
            var day = TimeSpan.FromDays(1);
            var activeChats = db.Posts.Where(x => x.Timestamp > now - activeWindow).Select(x => x.ChatId).Distinct().ToList();

            foreach (var chatId in activeChats.OrderBy(_ => Random.Shared.Next()))
                await HandleOneChat(db, now, day, chatId);
        }

        private readonly TimeSpan _delay = TimeSpan.FromSeconds(5);

        private async Task HandleOneChat(SqliteDb db, DateTime now, TimeSpan day, long chatId)
        {
            try
            {
                await HandleOneChatCore(db, now, day, chatId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HandleOneChat");
            }
        }

        private async Task HandleOneChatCore(SqliteDb db, DateTime now, TimeSpan day, long chatId)
        {
            var chat = await _botClient.GetChat(chatId);
            _logger.LogDebug("Chat {Title}", chat.Title);
            var topPosts = db
                .Posts.Where(x => x.ChatId == chatId && x.Timestamp > now - day)
                .OrderByDescending(x => x.Interactions.Select(i => i.Reaction ? 1 : -1).Sum())
                .ThenBy(x => x.Id)
                .Take(20)
                .LoadWith(x => x.Interactions)
                .ToList()
                .Where(x => x.Interactions.Select(i => i.Reaction ? 1 : -1).Sum() > 0)
                .ToList();
            var previousTop = db.TopPostsDays.Where(x => x.ChatId == chatId).ToList();
            var messageIds = previousTop.Select(x => x.PostId).ToList();
            var previousTopPostsDb = db
                .Posts.Where(x => x.ChatId == chatId && messageIds.Contains(x.MessageId))
                .LoadWith(x => x.Interactions)
                .ToList();

            var interestingUsers = topPosts.Select(x => x.PosterId).Concat(previousTopPostsDb.Select(x => x.PosterId)).Distinct().ToList();
            var userIdToUser = await TelegramHelper.GetTelegramUsers(chat, interestingUsers, _botClient);

            var noLongerTop = previousTop.Where(x => !topPosts.Select(post => post.MessageId).Contains(x.PostId));

            foreach (var post in noLongerTop)
            {
                await NoLongerTopPost(db, chatId, previousTopPostsDb, userIdToUser, post);
                await Task.Delay(_delay);
            }

            var newTop = topPosts.Where(x => !previousTop.Select(tpd => tpd.Id).Contains(x.MessageId));
            foreach (var post in newTop)
            {
                await NewTopPost(db, chatId, userIdToUser, post);
                await Task.Delay(_delay);
            }
        }

        private async Task NoLongerTopPost(
            SqliteDb db,
            long chatId,
            List<Post> previousTopDb,
            Dictionary<long, User> userIdToUser,
            TopPostsDay post
        )
        {
            try
            {
                await NoLongerTopPostCore(db, chatId, previousTopDb, userIdToUser, post);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during noLongerTop iteration, chatId {ChatId} postId {PostId}", post.ChatId, post.PostId);
            }
        }

        private async Task NoLongerTopPostCore(
            SqliteDb db,
            long chatId,
            List<Post> previousTopDb,
            Dictionary<long, User> userIdToUser,
            TopPostsDay post
        )
        {
            try
            {
                var prevTopDb = previousTopDb.SingleOrDefault(x => x.MessageId == post.PostId);
                var caption = prevTopDb?.ReplyMessageId == null;
                var ikm = ConstructReplyMarkup(prevTopDb);

                if (caption)
                {
                    var userOk = false;
                    User? user = null;
                    if (prevTopDb != null)
                        userOk = userIdToUser.TryGetValue(prevTopDb.PosterId, out user);
                    if (userOk)
                    {
                        await _polly.MessageEdit.ExecuteAsync(
                            async (ct) =>
                                await _botClient.EditMessageCaption(
                                    chatId,
                                    (int)post.PostId,
                                    TelegramHelper.MentionUsername(user!),
                                    parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2,
                                    replyMarkup: ikm,
                                    cancellationToken: ct
                                )
                        );
                    }
                    else
                    {
                        if (prevTopDb == null)
                            await db.TopPostsDays.DeleteAsync(x => x.Id == post.Id);
                        await _polly.MessageEdit.ExecuteAsync(
                            async (ct) =>
                                await _botClient.EditMessageCaption(
                                    chatId,
                                    (int)post.PostId,
                                    "От покинувшего чат пользователя",
                                    replyMarkup: ikm,
                                    cancellationToken: ct
                                )
                        );
                    }
                }
                else
                {
                    await _polly.MessageEdit.ExecuteAsync(
                        async (ct) =>
                            await _botClient.EditMessageText(
                                chatId,
                                (int)post.PostId,
                                "Оценить альбом",
                                replyMarkup: ikm,
                                cancellationToken: ct
                            )
                    );
                }
            }
            catch (ApiRequestException e) when (e.Message.Contains("message to edit not found"))
            {
                _logger.LogInformation("Somebody deleted the message already, just remove from database");
            }
            await db.TopPostsDays.DeleteAsync(x => x.Id == post.Id);
        }

        private static InlineKeyboardMarkup ConstructReplyMarkup(Post? prevTopDb)
        {
            var interactions = prevTopDb?.Interactions.ToList();
            var ikm = InlineKeyboardMarkup.Empty();
            if (interactions != null)
            {
                var likes = interactions.Count(x => x.Reaction);
                var dislikes = interactions.Count - likes;
                ikm = new InlineKeyboardMarkup(
                    new InlineKeyboardButton(likes > 0 ? $"{likes} 👍" : "👍") { CallbackData = "+" },
                    new InlineKeyboardButton(dislikes > 0 ? $"{dislikes} 👎" : "👎") { CallbackData = "-" }
                );
            }

            return ikm;
        }

        private async Task NewTopPost(SqliteDb db, long chatId, Dictionary<long, User> userIdToUser, Post post)
        {
            try
            {
                await NewTopPostCore(db, chatId, userIdToUser, post);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception during NewTopPost iteration");
            }
        }

        private async Task NewTopPostCore(SqliteDb db, long chatId, Dictionary<long, User> userIdToUser, Post post)
        {
            var ikm = ConstructReplyMarkup(post);
            var caption = post.ReplyMessageId == null;
            if (caption)
            {
                var userOk = userIdToUser.TryGetValue(post.PosterId, out var user);
                if (userOk)
                {
                    await _polly.MessageEdit.ExecuteAsync(
                        async (ct) =>
                            await _botClient.EditMessageCaption(
                                chatId,
                                (int)post.MessageId,
                                $"{TelegramHelper.MentionUsername(user!)}{Environment.NewLine}{Environment.NewLine}\\{TopOfTheDayHashTag}",
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2,
                                replyMarkup: ikm,
                                cancellationToken: ct
                            )
                    );
                }
                else
                {
                    await _polly.MessageEdit.ExecuteAsync(
                        async (ct) =>
                            await _botClient.EditMessageCaption(
                                chatId,
                                (int)post.MessageId,
                                $"От покинувшего чат пользователя{Environment.NewLine}{Environment.NewLine}\\{TopOfTheDayHashTag}",
                                parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2,
                                replyMarkup: ikm,
                                cancellationToken: ct
                            )
                    );
                }
            }
            else
            {
                await _polly.MessageEdit.ExecuteAsync(
                    async (ct) =>
                        await _botClient.EditMessageText(
                            chatId,
                            (int)post.MessageId,
                            $"Оценить альбом{Environment.NewLine}{Environment.NewLine}{TopOfTheDayHashTag}",
                            replyMarkup: ikm,
                            cancellationToken: ct
                        )
                );
            }
            await db.InsertAsync(new TopPostsDay { ChatId = chatId, PostId = post.MessageId });
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _ = MainLoop();
            return Task.CompletedTask;
        }
    }
}
