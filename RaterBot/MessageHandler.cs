using System.Diagnostics;
using System.Runtime.Caching;
using System.Text;
using LinqToDB;
using RaterBot.Database;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

namespace RaterBot;

internal sealed class MessageHandler
{
    private readonly SqliteDb _sqliteDb;
    private readonly ITelegramBotClient _botClient;
    private readonly ILogger<MessageHandler> _logger;

    public MessageHandler(ITelegramBotClient botClient, SqliteDb sqliteDb, ILogger<MessageHandler> logger)
    {
        _sqliteDb = sqliteDb;
        _botClient = botClient;
        _logger = logger;
    }

    private static readonly InlineKeyboardMarkup _newPostIkm =
        new(
            new[]
            {
                new InlineKeyboardButton("👍") { CallbackData = "+" },
                new InlineKeyboardButton("👎") { CallbackData = "-" }
            }
        );

    private static readonly HashSet<char> _shouldBeEscaped =
        new() { '\\', '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

    public async Task HandleUpdate(User me, Update update)
    {
        Debug.Assert(me.Username != null);
        try
        {
            if (update.Type == UpdateType.CallbackQuery)
            {
                await HandleCallbackData(update);
                return;
            }

            if (update.Type == UpdateType.Message)
            {
                var msg = update.Message;
                if (msg!.Text != null)
                {
                    if (IsBotCommand(me.Username, msg.Text, "/delete"))
                    {
                        await HandleDelete(update, me);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/top_posts_day"))
                    {
                        await HandleTopPosts(update, Period.Day);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/top_posts_week"))
                    {
                        await HandleTopPosts(update, Period.Week);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/top_posts_month"))
                    {
                        await HandleTopPosts(update, Period.Month);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/top_authors_week"))
                    {
                        await HandleTopAuthors(update, Period.Week);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/top_authors_month"))
                    {
                        await HandleTopAuthors(update, Period.Month);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/controversial_month"))
                    {
                        await HandleControversial(update, Period.Month);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/controversial_week"))
                    {
                        await HandleControversial(update, Period.Week);
                        return;
                    }

                    if (IsBotCommand(me.Username, msg.Text, "/text"))
                    {
                        if (msg.ReplyToMessage?.From?.Id == me.Id)
                        {
                            ReplyAndDeleteLater(
                                msg,
                                "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку не от бота"
                            );
                            return;
                        }

                        if (string.IsNullOrWhiteSpace(msg.ReplyToMessage?.Text))
                        {
                            ReplyAndDeleteLater(
                                msg,
                                "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку"
                            );
                            return;
                        }

                        await HandleTextReplyAsync(update);
                        return;
                    }

                    var (type, url) = FindSupportedSiteLink(msg);
                    switch (type)
                    {
                        case UrlType.Vk:
                        case UrlType.TikTok:
                        case UrlType.Youtube:
                            await HandleYtDlp(update, url!, type);
                            return;
                        case UrlType.Reddit:
                        case UrlType.Twitter:
                        case UrlType.Instagram:
                            await HandleGalleryDl(update, url!);
                            break;
                        case UrlType.NotFound:
                        default:
                            break;
                    }
                }

                if (
                    msg.Type is MessageType.Photo or MessageType.Video
                    || (
                        msg.Type == MessageType.Document
                        && msg.Document?.MimeType != null
                        && (msg.Document.MimeType.StartsWith("image") || msg.Document.MimeType.StartsWith("video"))
                    )
                )
                {
                    if (msg.ReplyToMessage != null)
                    {
                        _logger.LogInformation("Reply media messages should be ignored");
                        return;
                    }

                    if (msg.MediaGroupId != null)
                        await HandleMediaGroup(msg);
                    else
                        await HandleMediaMessage(msg);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"General update exception inside {nameof(HandleUpdate)}");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static (UrlType, Uri?) FindSupportedSiteLink(Message msg)
    {
        if (msg.Text == null || msg.Entities == null)
            return (UrlType.NotFound, null);
        var entities = msg.Entities.Where(e => e.Type == MessageEntityType.Url);

        foreach (var entity in entities)
        {
            var urlText = msg.Text[entity.Offset..(entity.Offset + entity.Length)];
            var url = new Uri(urlText);
            var host = url.Host;
            if (host.EndsWith("tiktok.com"))
                return (UrlType.TikTok, url);
            if (host.EndsWith("vk.com"))
                return (UrlType.Vk, url);
            if (host.EndsWith("twitter.com"))
                return (UrlType.Twitter, url);
            if (host.EndsWith("instagram.com"))
                return (UrlType.Instagram, url);
            if (host.EndsWith("reddit.com"))
                return (UrlType.Reddit, url);
            if (host.EndsWith("youtube.com") && urlText.Contains("youtube.com/shorts"))
                return (UrlType.Youtube, url);
        }

        return (UrlType.NotFound, null);
    }

    private async Task HandleDelete(Update update, User bot)
    {
        var msg = update.Message;
        Debug.Assert(msg != null);
        if (msg.ReplyToMessage == null)
        {
            ReplyAndDeleteLater(msg, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
            return;
        }
        Debug.Assert(msg.ReplyToMessage.From != null);
        if (msg.ReplyToMessage.From.Id != bot.Id)
        {
            ReplyAndDeleteLater(msg, "Эту команду нужно вызывать реплаем на сообщение бота");
            return;
        }
        var post = _sqliteDb.Posts
            .Where(p => p.ChatId == msg.Chat.Id && p.MessageId == msg.ReplyToMessage.MessageId)
            .FirstOrDefault();
        if (post == null)
        {
            ReplyAndDeleteLater(msg, "Это сообщение нельзя удалить");
            return;
        }
        Debug.Assert(msg.From != null);
        if (post.PosterId != msg.From.Id)
        {
            ReplyAndDeleteLater(msg, "Нельзя удалить чужой пост");
            return;
        }
        if (post.Timestamp + TimeSpan.FromHours(1) < DateTime.UtcNow)
        {
            ReplyAndDeleteLater(msg, "Этот пост слишком старый, чтобы его удалять");
            return;
        }
        await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.ReplyToMessage.MessageId);
        await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        _sqliteDb.Interactions.Where(i => i.PostId == post.Id).Delete();
        _sqliteDb.Posts.Where(p => p.Id == post.Id).Delete();
    }

    private async Task HandleControversial(Update update, Period period)
    {
        Debug.Assert(update.Message != null);
        var chat = update.Message.Chat;

        if (chat.Type != ChatType.Supergroup && string.IsNullOrWhiteSpace(chat.Username))
        {
            await _botClient.SendTextMessageAsync(
                chat.Id,
                "Этот чат не является супергруппой и не имеет имени: нет возможности оставлять ссылки на посты"
            );
            _logger.LogInformation($"{nameof(HandleControversial)} - unable to link top posts, skipping");
            return;
        }

        var posts = _sqliteDb.Posts
            .Where(p => p.ChatId == chat.Id && p.Timestamp > DateTime.UtcNow - PeriodToTimeSpan(period))
            .LoadWith(p => p.Interactions)
            .ToList();

        var controversialPosts = posts
            .Select(
                p =>
                    new
                    {
                        Post = p,
                        Likes = p.Interactions.Count(i => i.Reaction),
                        Dislikes = p.Interactions.Count(i => !i.Reaction),
                        Magnitude = p.Interactions.Count()
                    }
            )
            .OrderByDescending(x => x.Magnitude * (double)Math.Min(x.Dislikes, x.Likes) / Math.Max(x.Dislikes, x.Likes))
            .ThenByDescending(x => x.Dislikes)
            .Take(20)
            .ToList();

        var userIds = controversialPosts.Select(x => x.Post.PosterId).Distinct().ToList();
        var userIdToUser = await GetTelegramUsers(chat, userIds);

        var message = new StringBuilder(1024);
        message.Append("Топ противоречивых постов за ");
        message.Append(ForLast(period));
        message.Append(':');
        message.Append(Environment.NewLine);
        var i = 0;
        var sg = chat.Type == ChatType.Supergroup;
        foreach (var item in controversialPosts)
        {
            AppendPlace(message, i);
            var knownUser = userIdToUser.TryGetValue(item.Post.PosterId, out var user);

            message.Append("[От ");
            if (knownUser)
                message.Append($"{UserEscaped(user!)}](");
            else
                message.Append("покинувшего чат пользователя](");

            var link = sg
                ? LinkToSuperGroupMessage(chat, item.Post.MessageId)
                : LinkToGroupWithNameMessage(chat, item.Post.MessageId);
            message.Append(link);
            message.Append(")");
            i++;
        }

        ReplyAndDeleteLater(update.Message, message.ToString(), ParseMode.MarkdownV2);
    }

    private async Task HandleTopAuthors(Update update, Period period)
    {
        Debug.Assert(update.Message != null);
        var chat = update.Message.Chat;
        var posts = _sqliteDb.Posts
            .Where(p => p.ChatId == update.Message.Chat.Id && p.Timestamp > DateTime.UtcNow - PeriodToTimeSpan(period))
            .LoadWith(p => p.Interactions)
            .ToList();

        if (!posts.SelectMany(x => x.Interactions).Where(i => i.Reaction).Any())
        {
            await _botClient.SendTextMessageAsync(chat.Id, $"Не найдено заплюсованных постов за {ForLast(period)}");
            return;
        }

        var postWithLikes = posts.Select(p => new { Post = p, Likes = p.Interactions.Sum(i => i.Reaction ? 1 : -1) });

        var topAuthors = postWithLikes
            .GroupBy(x => x.Post.PosterId)
            .Select(
                g =>
                    new
                    {
                        PosterId = g.Key,
                        Likes = g.Sum(x => x.Likes),
                        HirschIndex = g.OrderByDescending(x => x.Likes)
                            .TakeWhile((x, iter) => x.Likes >= iter + 1)
                            .Count()
                    }
            )
            .OrderByDescending(x => x.HirschIndex)
            .ThenByDescending(x => x.Likes)
            .Take(20)
            .ToList();

        var userIds = topAuthors.Select(x => x.PosterId).ToList();
        var userIdToUser = await GetTelegramUsers(chat, userIds);

        var message = new StringBuilder(1024);
        message.Append("Топ авторов за ");
        message.Append(ForLast(period));
        message.Append(':');
        message.Append(Environment.NewLine);
        var i = 0;
        foreach (var item in topAuthors)
        {
            AppendPlace(message, i);

            var knownUser = userIdToUser.TryGetValue(item.PosterId, out var user);
            message.Append(knownUser ? GetFirstLastName(user!) : "покинувший чат пользователь");
            message.Append($" очков: {item.HirschIndex}, апвоутов: {item.Likes}");

            i++;
        }

        ReplyAndDeleteLater(update.Message, message.ToString());
    }

    private async Task HandleTopPosts(Update update, Period period)
    {
        Debug.Assert(update.Message != null);
        var chat = update.Message.Chat;

        if (chat.Type != ChatType.Supergroup && string.IsNullOrWhiteSpace(chat.Username))
        {
            await _botClient.SendTextMessageAsync(
                chat.Id,
                "Этот чат не является супергруппой и не имеет имени: нет возможности оставлять ссылки на посты"
            );
            _logger.LogInformation($"{nameof(HandleTopPosts)} - unable to link top posts, skipping");
            return;
        }

        var posts = _sqliteDb.Posts
            .Where(p => p.ChatId == chat.Id && p.Timestamp > DateTime.UtcNow - PeriodToTimeSpan(period))
            .LoadWith(p => p.Interactions)
            .ToList();

        if (!posts.SelectMany(p => p.Interactions).Any())
        {
            await _botClient.SendTextMessageAsync(chat.Id, $"Не найдено заплюсованных постов за {ForLast(period)}");
            _logger.LogInformation($"{nameof(HandleTopPosts)} - no upvoted posts, skipping");
            return;
        }

        var topPosts = posts
            .Select(p => new { Post = p, Likes = p.Interactions.Sum(i => i.Reaction ? 1 : -1) })
            .OrderByDescending(x => x.Likes)
            .Take(20)
            .ToList();

        var userIds = topPosts.Select(x => x.Post.PosterId).Distinct().ToList();
        var userIdToUser = await GetTelegramUsers(chat, userIds);

        var message = new StringBuilder(1024);
        message.Append("Топ постов за ");
        message.Append(ForLast(period));
        message.Append(':');
        message.Append(Environment.NewLine);
        var i = 0;
        var sg = chat.Type == ChatType.Supergroup;
        foreach (var item in topPosts)
        {
            if (item.Likes <= 0)
                break;

            AppendPlace(message, i);
            var knownUser = userIdToUser.TryGetValue(item.Post.PosterId, out var user);

            message.Append("[От ");
            if (knownUser)
                message.Append($"{UserEscaped(user!)}](");
            else
                message.Append("покинувшего чат пользователя](");

            var link = sg
                ? LinkToSuperGroupMessage(chat, item.Post.MessageId)
                : LinkToGroupWithNameMessage(chat, item.Post.MessageId);
            message.Append(link);
            message.Append(") ");
            if (item.Likes > 0)
                message.Append("\\+");
            message.Append(item.Likes);
            i++;
        }

        ReplyAndDeleteLater(update.Message, message.ToString(), ParseMode.MarkdownV2);
    }

    private async Task<Dictionary<long, User>> GetTelegramUsers(Chat chat, ICollection<long> userIds)
    {
        var userIdToUser = new Dictionary<long, User>(userIds.Count);
        foreach (var id in userIds)
        {
            if (MemoryCache.Default.Get(id.ToString()) is User fromCache)
            {
                userIdToUser[id] = fromCache;
                continue;
            }

            try
            {
                var member = await _botClient.GetChatMemberAsync(chat.Id, id);
                userIdToUser[id] = member.User;
                MemoryCache.Default.Add(
                    id.ToString(),
                    member,
                    new CacheItemPolicy { SlidingExpiration = TimeSpan.FromHours(1) }
                );
            }
            catch (ApiRequestException)
            {
                // User not found for any reason, we don't care.
            }
        }

        return userIdToUser;
    }

    private void ReplyAndDeleteLater(Message message, string text, ParseMode? parseMode = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var m = await _botClient.SendTextMessageAsync(
                    message.Chat.Id,
                    text,
                    replyToMessageId: message.MessageId,
                    disableNotification: true,
                    parseMode: parseMode
                );
                await Task.Delay(TimeSpan.FromMinutes(10));
                await _botClient.DeleteMessageAsync(message.Chat.Id, m.MessageId);
            }
            catch (ApiRequestException are)
            {
                _logger.LogError(are, "Unable to send {Text} with parseMode = {ParseMode}", text, parseMode);
            }
            await _botClient.DeleteMessageAsync(message.Chat.Id, message.MessageId);
        });
    }

    private static bool IsBotCommand(string username, string? msgText, string command) =>
        msgText != null && (msgText == command || msgText == $"{command}@{username}");

    private static void AppendPlace(StringBuilder stringBuilder, int i)
    {
        switch (i)
        {
            case 0:
                stringBuilder.Append("🥇 ");
                break;
            case 1:
                stringBuilder.Append($"{Environment.NewLine}🥈 ");
                break;
            case 2:
                stringBuilder.Append($"{Environment.NewLine}🥉 ");
                break;
            default:
                stringBuilder.Append($"{Environment.NewLine}{i + 1} ");
                break;
        }
    }

    private static string LinkToSuperGroupMessage(Chat chat, long messageId) =>
        $"https://t.me/c/{chat.Id.ToString()[4..]}/{messageId}";

    private static string LinkToGroupWithNameMessage(Chat chat, long messageId) =>
        $"https://t.me/{chat.Username}/{messageId}";

    private async Task HandleCallbackData(Update update)
    {
        Debug.Assert(update.CallbackQuery != null);
        var msg = update.CallbackQuery.Message;
        Debug.Assert(msg != null);

        var updateData = update.CallbackQuery.Data;
        if (updateData != "-" && updateData != "+")
        {
            _logger.LogWarning("Invalid callback query data: {Data}", updateData);
            return;
        }

        _logger.LogDebug("Valid callback request");
        var post = _sqliteDb.Posts
            .Where(p => p.ChatId == msg.Chat.Id && p.MessageId == msg.MessageId)
            .LoadWith(p => p.Interactions)
            .SingleOrDefault();
        if (post == null)
        {
            _logger.LogError(
                "Cannot find post in the database, ChatId = {ChatId}, MessageId = {MessageId}",
                msg.Chat.Id,
                msg.MessageId
            );
            try
            {
                await _botClient.EditMessageReplyMarkupAsync(msg.Chat.Id, msg.MessageId, InlineKeyboardMarkup.Empty());
            }
            catch (ApiRequestException e)
            {
                _logger.LogWarning(e, "Unable to set empty reply markup, trying to delete post");
                await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }
            return;
        }

        if (post.PosterId == update.CallbackQuery.From.Id)
        {
            await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Нельзя голосовать за свои посты!");
            return;
        }

        var interactions = post.Interactions.ToList();
        var interaction = interactions.SingleOrDefault(i => i.UserId == update.CallbackQuery.From.Id);

        var newReaction = updateData == "+";
        if (interaction != null)
        {
            if (newReaction == interaction.Reaction)
            {
                var reaction = newReaction ? "👍" : "👎";
                await _botClient.AnswerCallbackQueryAsync(
                    update.CallbackQuery.Id,
                    $"Ты уже поставил(-а) {reaction} этому посту"
                );
                _logger.LogInformation("No need to update reaction");
                return;
            }
            _sqliteDb.Interactions.Where(i => i.Id == interaction.Id).Set(i => i.Reaction, newReaction).Update();
            interaction.Reaction = newReaction;
        }
        else
        {
            interaction = new()
            {
                Reaction = newReaction,
                UserId = update.CallbackQuery.From.Id,
                PostId = post.Id
            };
            _sqliteDb.Insert(interaction);
            interactions.Add(interaction);
        }

        var likes = interactions.Count(i => i.Reaction);
        var dislikes = interactions.Count - likes;

        if (DateTime.UtcNow.AddMinutes(-5) > post.Timestamp && dislikes > 2 * likes + 3)
        {
            _logger.LogInformation("Deleting post. Dislikes = {Dislikes}, Likes = {Likes}", dislikes, likes);
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            _sqliteDb.Interactions.Delete(i => i.PostId == post.Id);
            _sqliteDb.Posts.Delete(p => p.Id == post.Id);
            await _botClient.AnswerCallbackQueryAsync(
                update.CallbackQuery.Id,
                "Твой голос стал решающей каплей, этот пост удалён"
            );
            return;
        }

        var plusText = likes > 0 ? $"{likes} 👍" : "👍";
        var minusText = dislikes > 0 ? $"{dislikes} 👎" : "👎";

        var ikm = new InlineKeyboardMarkup(
            new[]
            {
                new(plusText) { CallbackData = "+" },
                new InlineKeyboardButton(minusText) { CallbackData = "-" }
            }
        );

        try
        {
            await _botClient.EditMessageReplyMarkupAsync(msg.Chat.Id, msg.MessageId, ikm);
            await _botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EditMessageReplyMarkupAsync");
        }
    }

    private async Task HandleGalleryDl(Update update, Uri link)
    {
        _logger.LogInformation("New HandleGalleryDl message");

        var msg = update.Message;
        Debug.Assert(msg != null);
        var from = msg.From;
        Debug.Assert(from != null);
        var msgText = msg.Text;
        Debug.Assert(msgText != null);

        var processingMsg = await _botClient.SendTextMessageAsync(
            msg.Chat.Id,
            "Processing...",
            replyToMessageId: msg.MessageId
        );

        var disposeMe = Array.Empty<Stream>();
        try
        {
            var fileList = await DownloadHelper.DownloadGalleryDl(link);
            if (!fileList.Any())
                return;

            var album = fileList.Length > 1;
            var photo = Path.GetExtension(fileList.First()) is ".jpg" or ".png";
            disposeMe = fileList.Select(f => System.IO.File.Open(f, FileMode.Open, FileAccess.Read)).ToArray();

            if (album)
            {
                var caption = MentionUsername(from);
                var newMessage = await _botClient.SendMediaGroupAsync(
                    msg.Chat.Id,
                    disposeMe
                        .Take(10)
                        .Select(
                            (x, i) =>
                                // Videos cannot be album in Twitter, so we assume it's photo
                                new InputMediaPhoto(new InputMedia(x, Path.GetFileName(fileList[i])))
                                {
                                    Caption = caption,
                                    ParseMode = ParseMode.MarkdownV2
                                }
                        )
                );
                var rateMessage = await _botClient.SendTextMessageAsync(
                    msg.Chat.Id,
                    "Оценить альбом",
                    replyMarkup: _newPostIkm,
                    replyToMessageId: newMessage.First().MessageId
                );
                InsertIntoPosts(msg.Chat.Id, from.Id, rateMessage.MessageId);
            }
            else if (photo)
            {
                var newMessage = await _botClient.SendPhotoAsync(
                    msg.Chat.Id,
                    new InputOnlineFile(disposeMe.First()),
                    replyMarkup: _newPostIkm,
                    caption: MentionUsername(from),
                    parseMode: ParseMode.MarkdownV2
                );
                InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
            }
            else
            {
                var newMessage = await _botClient.SendVideoAsync(
                    msg.Chat.Id,
                    new InputOnlineFile(disposeMe.First()),
                    replyMarkup: _newPostIkm,
                    caption: MentionUsername(from),
                    parseMode: ParseMode.MarkdownV2
                );
                InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
            }

            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, nameof(HandleGalleryDl));
        }
        finally
        {
            foreach (var fileStream in disposeMe)
                fileStream.Dispose();
            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, processingMsg.MessageId);
        }
    }

    private async Task HandleYtDlp(Update update, Uri videoLink, UrlType urlType)
    {
        _logger.LogInformation("New YtDlp supported message");

        var msg = update.Message;
        Debug.Assert(msg != null);
        var from = msg.From;
        Debug.Assert(from != null);
        var msgText = msg.Text;
        Debug.Assert(msgText != null);

        var processingMsg = await _botClient.SendTextMessageAsync(
            msg.Chat.Id,
            "Processing...",
            replyToMessageId: msg.MessageId
        );

        try
        {
            var tempFileName = DownloadHelper.DownloadYtDlp(videoLink, urlType);
            if (tempFileName == null)
            {
                _logger.LogInformation("Could not download the video, check logs");
                return;
            }

            await using (var stream = System.IO.File.Open(tempFileName, FileMode.Open, FileAccess.Read))
            {
                var newMessage = await _botClient.SendVideoAsync(
                    msg.Chat.Id,
                    new InputOnlineFile(stream),
                    replyMarkup: _newPostIkm,
                    caption: MentionUsername(from),
                    parseMode: ParseMode.MarkdownV2
                );
                InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
            }

            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, nameof(HandleYtDlp));
        }
        finally
        {
            _ = _botClient.DeleteMessageAsync(msg.Chat.Id, processingMsg.MessageId);
        }
    }

    private async Task HandleTextReplyAsync(Update update)
    {
        _logger.LogInformation("New valid text message");
        var msg = update.Message;
        Debug.Assert(msg != null);
        var replyTo = msg.ReplyToMessage;
        Debug.Assert(replyTo != null);
        var from = replyTo.From;
        Debug.Assert(from != null);

        var newMessage = await _botClient.SendTextMessageAsync(
            msg.Chat.Id,
            $"{AtMentionUsername(from)}:{Environment.NewLine}{replyTo.Text}",
            replyMarkup: _newPostIkm
        );
        try
        {
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        }
        catch (ApiRequestException are)
        {
            _logger.LogWarning(are, "Unable to delete message in HandleTextReplyAsync, duplicated update?");
        }

        if (msg.From?.Id == replyTo.From?.Id)
            await _botClient.DeleteMessageAsync(msg.Chat.Id, replyTo.MessageId);

        InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
    }

    private void InsertIntoPosts(long chatId, long posterId, long messageId)
    {
        _sqliteDb.Insert(
            new Post
            {
                ChatId = chatId,
                PosterId = posterId,
                MessageId = messageId,
                Timestamp = DateTime.UtcNow
            }
        );
    }

    private async Task HandleMediaMessage(Message msg)
    {
        _logger.LogInformation("New valid media message");
        var from = msg.From;
        Debug.Assert(from != null);
        try
        {
            var newMessage = await _botClient.CopyMessageAsync(
                msg.Chat.Id,
                msg.Chat.Id,
                msg.MessageId,
                replyMarkup: _newPostIkm,
                caption: MentionUsername(from),
                parseMode: ParseMode.MarkdownV2
            );
            InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.Id);
            await _botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot handle media message");
        }
    }

    private async Task HandleMediaGroup(Message msg)
    {
        Debug.Assert(msg.MediaGroupId != null);
        _logger.LogInformation("New valid media group");

        var from = msg.From;
        Debug.Assert(from != null);
        try
        {
            var newMessage = await _botClient.SendTextMessageAsync(
                msg.Chat.Id,
                "Оценить альбом",
                replyToMessageId: msg.MessageId,
                replyMarkup: _newPostIkm
            );
            InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot handle media group");
        }
    }

    private static string MentionUsername(User user)
    {
        var whoEscaped = UserEscaped(user);
        return $"[От {whoEscaped}](tg://user?id={user.Id})";
    }

    private static string UserEscaped(User user)
    {
        var who = GetFirstLastName(user);
        var whoEscaped = new StringBuilder(who.Length);
        foreach (var c in who)
        {
            if (_shouldBeEscaped.Contains(c))
                whoEscaped.Append('\\');
            whoEscaped.Append(c);
        }

        return whoEscaped.ToString();
    }

    private static string AtMentionUsername(User user)
    {
        if (string.IsNullOrWhiteSpace(user.Username))
        {
            var who = GetFirstLastName(user);
            return $"От {who} без ника в телеге";
        }

        return $"От @{user.Username}";
    }

    private static string GetFirstLastName(User user)
    {
        var last = user.LastName ?? string.Empty;
        var who = $"{user.FirstName} {last}".Trim();
        if (string.IsNullOrWhiteSpace(who))
            who = "аноним";
        return who;
    }

    private static TimeSpan PeriodToTimeSpan(Period period)
    {
        return TimeSpan.FromDays(
            period switch
            {
                Period.Day => 1,
                Period.Week => 7,
                Period.Month => 30,
                _ => throw new ArgumentException("Enum out of range", nameof(period))
            }
        );
    }

    private static string ForLast(Period period)
    {
        return period switch
        {
            Period.Day => "последний день",
            Period.Week => "последнюю неделю",
            Period.Month => "последний месяц",
            _ => throw new ArgumentException("Enum out of range", nameof(period))
        };
    }

    private enum Period
    {
        Day,
        Week,
        Month
    }
}
