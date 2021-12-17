using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using Serilog;
using Serilog.Core;
using System.Diagnostics;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace RaterBot
{
    internal sealed class Program
    {
        private static readonly Logger _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

        static readonly ITelegramBotClient botClient = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API") ?? throw new Exception("TELEGRAM_MEDIA_RATER_BOT_API enviroment variable not set"));
        const int updateLimit = 100;
        const int timeout = 1800;
        private const string dbPath = "sqlite.db";

        private static readonly Lazy<SqliteConnection> _dbConnection = new(() => new SqliteConnection(_connectionString));
        private static readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ConnectionString;
        private static readonly string _migrationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ConnectionString;

        private static readonly InlineKeyboardMarkup _newPostIkm = new(new InlineKeyboardButton[]
        {
            new InlineKeyboardButton("👍"){ CallbackData = "+" },
            new InlineKeyboardButton("👎"){ CallbackData = "-" }
        });

        private static void InitAndMigrateDb()
        {
            SQLitePCL.Batteries.Init();

            var serviceProvider = CreateServices();
            using var scope = serviceProvider.CreateScope();
            MigrateDatabase(scope.ServiceProvider);

            var con = _dbConnection.Value;
            con.Execute("PRAGMA synchronous = NORMAL;");
            con.Execute("PRAGMA vacuum;");
            con.Execute("PRAGMA temp_store = memory;");
        }

        static async Task Main()
        {
            InitAndMigrateDb();

            var me = await botClient.GetMeAsync();

            var offset = 0;
            while (true)
            {
                try
                {
                    var updates = await botClient.GetUpdatesAsync(offset, updateLimit, timeout);
                    if (updates?.Any() == true)
                    {
                        foreach (var update in updates)
                            await HandleUpdate(me, update);

                        offset = updates.Max(u => u.Id) + 1;

                        if (offset % 50 == 0) // Optimize sometimes
                            await _dbConnection.Value.ExecuteAsync("PRAGMA optimize;");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "General update exception");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }

        private static async Task HandleUpdate(User me, Update update)
        {
            Debug.Assert(me.Username != null);
            try
            {
                if (update.Type == Telegram.Bot.Types.Enums.UpdateType.CallbackQuery)
                    await HandleCallbackData(update);

                if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message)
                {
                    var msg = update.Message;
                    Debug.Assert(msg?.Text != null);

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

                    if (IsBotCommand(me.Username, msg.Text, "/text"))
                    {
                        if (msg.ReplyToMessage?.From?.Id == me.Id)
                        {
                            var m = await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку не от бота");
                            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(msg.ReplyToMessage?.Text))
                        {
                            var m = await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                            _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                            _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                            return;
                        }
                        await HandleTextReplyAsync(update);
                        return;
                    }

                    if (msg.Type == Telegram.Bot.Types.Enums.MessageType.Photo || msg.Type == Telegram.Bot.Types.Enums.MessageType.Video
                        || (msg.Type == Telegram.Bot.Types.Enums.MessageType.Document
                            && (msg.Document?.MimeType != null && (msg.Document.MimeType.StartsWith("image") || msg.Document.MimeType.StartsWith("video")))))
                    {
                        if (msg.ReplyToMessage != null)
                        {
                            _logger.Information("Reply media messages should be ignored");
                            return;
                        }
                        if (!string.IsNullOrWhiteSpace(msg.Caption) && (msg.Caption.Contains("/skip") || msg.Caption.Contains("/ignore") || msg.Caption.Contains("#skip") || msg.Caption.Contains("#ignore")))
                        {
                            _logger.Information("Media message that should be ignored");
                            return;
                        }
                        await HandleMediaMessage(msg);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "General update exception inside FOREACH loop");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        private static async Task HandleDelete(Update update, User bot)
        {
            var msg = update.Message;
            Debug.Assert(msg != null);
            if (msg.ReplyToMessage == null)
            {
                var m = await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                return;
            }
            var sqlParams = new { ChatId = msg.Chat.Id, msg.ReplyToMessage.MessageId };

            Debug.Assert(msg.ReplyToMessage.From != null);
            if (msg.ReplyToMessage.From.Id != bot.Id)
            {
                var m = await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на сообщение бота");
                _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                return;
            }

            var sql = $"SELECT * FROM {nameof(Post)} WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.MessageId)} = @MessageId";
            var post = await _dbConnection.Value.QueryFirstOrDefaultAsync<Post>(sql, sqlParams);
            if (post == null)
            {
                var m = await botClient.SendTextMessageAsync(msg.Chat, "Это сообщение нельзя удалить");
                _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                return;
            }

            Debug.Assert(msg.From != null);
            if (post.PosterId != msg.From.Id)
            {
                var m = await botClient.SendTextMessageAsync(msg.Chat, "Нельзя удалить чужой пост");
                _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                return;
            }

            if (post.Timestamp + TimeSpan.FromHours(1) < DateTime.UtcNow)
            {
                var m = await botClient.SendTextMessageAsync(msg.Chat, "Этот пост слишком старый, чтобы его удалять");
                _ = RemoveAfterSomeTime(msg.Chat, m.MessageId);
                _ = RemoveAfterSomeTime(msg.Chat, msg.MessageId);
                return;
            }

            await botClient.DeleteMessageAsync(msg.Chat, msg.ReplyToMessage.MessageId);
            await botClient.DeleteMessageAsync(msg.Chat, msg.MessageId);
            sql = $"DELETE FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
            await _dbConnection.Value.ExecuteAsync(sql, sqlParams);
            sql = $"DELETE FROM {nameof(Post)} WHERE {nameof(Post.ChatId)} = @ChatId AND {nameof(Post.MessageId)} = @MessageId;";
            await _dbConnection.Value.ExecuteAsync(sql, sqlParams);
        }

        private static readonly string _messageIdPlusCountPosterIdSql =
            $"SELECT {nameof(Interaction)}.{nameof(Interaction.MessageId)}, COUNT(*), {nameof(Interaction)}.{nameof(Interaction.PosterId)}" +
            $" FROM {nameof(Post)} INNER JOIN {nameof(Interaction)} ON {nameof(Post)}.{nameof(MessageId)} = {nameof(Interaction)}.{nameof(Interaction.MessageId)}" +
            $" WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Interaction)}.{nameof(Interaction.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.Timestamp)} > @TimeAgo AND {nameof(Interaction)}.{nameof(Interaction.Reaction)} = true" +
            $" GROUP BY {nameof(Interaction)}.{nameof(Interaction.MessageId)};";

        private static readonly string _messageIdMinusCountSql =
            $"SELECT {nameof(Interaction)}.{nameof(Interaction.MessageId)}, COUNT(*)" +
            $" FROM {nameof(Post)} INNER JOIN {nameof(Interaction)} ON {nameof(Post)}.{nameof(MessageId)} = {nameof(Interaction)}.{nameof(Interaction.MessageId)}" +
            $" WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Interaction)}.{nameof(Interaction.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.Timestamp)} > @TimeAgo AND {nameof(Interaction)}.{nameof(Interaction.Reaction)} = false" +
            $" GROUP BY {nameof(Interaction)}.{nameof(Interaction.MessageId)};";

        private static async Task HandleTopAuthors(Update update, Period period)
        {
            Debug.Assert(update.Message != null);
            var chat = update.Message.Chat;
            var sql = _messageIdPlusCountPosterIdSql;
            var sqlParams = new { TimeAgo = DateTime.UtcNow - PeriodToTimeSpan(period), ChatId = chat.Id };
            var plus = await _dbConnection.Value.QueryAsync<(long MessageId, long PlusCount, long PosterId)>(sql, sqlParams);
            if (!plus.Any())
            {
                await botClient.SendTextMessageAsync(chat, $"Не найдено заплюсованных постов за {ForLast(period)}");
                _logger.Information($"{nameof(HandleTopPosts)} - no upvoted posts, skipping");
                return;
            }
            sql = _messageIdMinusCountSql;
            var minus = (await _dbConnection.Value.QueryAsync<(long MessageId, long MinusCount)>(sql, sqlParams)).ToDictionary(x => x.MessageId, y => y.MinusCount);

            var topAuthors = plus.GroupBy(x => x.PosterId).Select(x => new
            {
                x.Key,
                Hindex = x.OrderByDescending(x => x.PlusCount).TakeWhile((z, i) => z.PlusCount >= i + 1).Count(),
                Likes = x.Sum(x => x.PlusCount)
            }).OrderByDescending(x => x.Hindex).ThenByDescending(x => x.Likes).Take(20);

            var userIds = topAuthors.Select(x => x.Key).Distinct();
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

                var knownUser = userIdToUser.TryGetValue(item.Key, out var user);
                if (knownUser)
                    message.Append(GetFirstLastName(user!));
                else
                    message.Append("покинувший чат пользователь");
                message.Append($" очков: {item.Hindex}, апвоутов: {item.Likes}");

                i++;
            }

            var m = await botClient.SendTextMessageAsync(chat, message.ToString());
            _ = RemoveAfterSomeTime(chat, update.Message.MessageId);
            _ = RemoveAfterSomeTime(chat, m.MessageId);
        }

        private static async Task HandleTopPosts(Update update, Period period)
        {
            Debug.Assert(update.Message != null);
            var chat = update.Message.Chat;

            if (chat.Type != Telegram.Bot.Types.Enums.ChatType.Supergroup && string.IsNullOrWhiteSpace(chat.Username))
            {
                await botClient.SendTextMessageAsync(chat, "Этот чат не является супергруппой и не имеет имени: нет возможности оставлять ссылки на посты");
                _logger.Information($"{nameof(HandleTopPosts)} - unable to link top posts, skipping");
                return;
            }

            var sql = _messageIdPlusCountPosterIdSql;
            var sqlParams = new { TimeAgo = DateTime.UtcNow - PeriodToTimeSpan(period), ChatId = chat.Id };
            var plusQuery = await _dbConnection.Value.QueryAsync<(long MessageId, long PlusCount, long PosterId)>(sql, sqlParams);
            var plus = plusQuery.ToDictionary(x => x.MessageId, x => x.PlusCount);
            var messageIdToUserId = plusQuery.ToDictionary(x => x.MessageId, x => x.PosterId);
            if (!plus.Any())
            {
                await botClient.SendTextMessageAsync(chat, $"Не найдено заплюсованных постов за {ForLast(period)}");
                _logger.Information($"{nameof(HandleTopPosts)} - no upvoted posts, skipping");
                return;
            }
            sql = _messageIdMinusCountSql;
            var minus = (await _dbConnection.Value.QueryAsync<(long MessageId, long MinusCount)>(sql, sqlParams)).ToDictionary(x => x.MessageId, y => y.MinusCount);

            var keys = plus.Keys.ToList();
            foreach (var key in keys)
                plus[key] -= minus.GetValueOrDefault(key);
            var topPosts = plus.OrderByDescending(x => x.Value).Take(20);

            var topTenWithUsers = topPosts.Select(x => x.Key).ToDictionary(x => x, x => new User());

            var userIds = topPosts.Select(x => messageIdToUserId[x.Key]).Distinct();
            var userIdToUser = await GetTelegramUsers(chat, userIds);

            var message = new StringBuilder(1024);
            message.Append("Топ постов за ");
            message.Append(ForLast(period));
            message.Append(':');
            message.Append(Environment.NewLine);
            var i = 0;
            var sg = chat.Type == Telegram.Bot.Types.Enums.ChatType.Supergroup;
            foreach (var item in topPosts)
            {
                AppendPlace(message, i);
                var knownUser = userIdToUser.TryGetValue(messageIdToUserId[item.Key], out var user);

                message.Append("От ");
                if (knownUser)
                    message.Append($"[{UserEscaped(user!)}](");
                else
                    message.Append("[покинувшего чат пользователя](");

                var link = sg ? LinkToSuperGroupMessage(chat, item.Key) : LinkToGroupWithNameMessage(chat, item.Key);
                message.Append(link);
                message.Append(") ");
                if (item.Value > 0)
                    message.Append("\\+");
                message.Append(item.Value);
                i++;
            }

            var m = await botClient.SendTextMessageAsync(chat, message.ToString(), Telegram.Bot.Types.Enums.ParseMode.MarkdownV2);
            _ = RemoveAfterSomeTime(chat, m.MessageId);
            _ = RemoveAfterSomeTime(chat, update.Message.MessageId);
        }

        private static async Task<Dictionary<long, User>> GetTelegramUsers(Chat chat, IEnumerable<long> userIds)
        {
            var userIdToUser = new Dictionary<long, User>();
            foreach (var id in userIds)
            {
                try
                {
                    var member = await botClient.GetChatMemberAsync(chat, id);
                    userIdToUser.Add(id, member.User);
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException)
                {
                    // User not found for any reason, we don't care.
                }
            }

            return userIdToUser;
        }

        private static async Task RemoveAfterSomeTime(Chat chat, int messageId)
        {
            await Task.Delay(TimeSpan.FromMinutes(10));
            await botClient.DeleteMessageAsync(chat, messageId);
        }

        private static bool IsBotCommand(string username, string? msgText, string command)
            => msgText != null && (msgText == command || msgText == $"{command}@{username}");

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

        private static string LinkToSuperGroupMessage(Chat chat, long messageId)
            => $"https://t.me/c/{chat.Id.ToString()[4..]}/{messageId}";

        private static string LinkToGroupWithNameMessage(Chat chat, long messageId)
            => $"https://t.me/{chat.Username}/{messageId}";

        private static async Task HandleCallbackData(Update update)
        {
            Debug.Assert(update.CallbackQuery != null);
            var msg = update.CallbackQuery.Message;
            Debug.Assert(msg != null);
            var connection = _dbConnection.Value;
            var chatAndMessageIdParams = new { ChatId = msg.Chat.Id, msg.MessageId };
            var updateData = update.CallbackQuery.Data;
            if (updateData != "-" && updateData != "+")
            {
                _logger.Warning("Invalid callback query data: {Data}", updateData);
                return;
            }

            _logger.Debug("Valid callback request");
            var sql = $"SELECT * FROM {nameof(Post)} WHERE {nameof(Post.ChatId)} = @ChatId AND {nameof(Post.MessageId)} = @MessageId;";
            var post = await connection.QuerySingleOrDefaultAsync<Post>(sql, new { ChatId = msg.Chat.Id, msg.MessageId });
            if (post == null)
            {
                _logger.Error("Cannot find post in the database, ChatId = {ChatId}, MessageId = {MessageId}", msg.Chat.Id, msg.MessageId);
                try
                {
                    await botClient.EditMessageReplyMarkupAsync(msg.Chat.Id, msg.MessageId, InlineKeyboardMarkup.Empty());
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException e)
                {
                    _logger.Warning(e, "Unable to set empty reply markup, trying to delete post");
                    await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                }
                sql = $"SELECT * FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
                await connection.QueryAsync<Interaction>(sql, chatAndMessageIdParams);
                return;
            }

            if (post.PosterId == update.CallbackQuery.From.Id)
            {
                await botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Нельзя голосовать за свои посты!");
                return;
            }

            sql = $"SELECT * FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
            var interactions = (await connection.QueryAsync<Interaction>(sql, chatAndMessageIdParams)).ToList();
            var interaction = interactions.SingleOrDefault(i => i.UserId == update.CallbackQuery.From.Id);

            var newReaction = updateData == "+";
            if (interaction != null)
            {
                if (newReaction == interaction.Reaction)
                {
                    var reaction = newReaction ? "👍" : "👎";
                    await botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, $"Ты уже поставил {reaction} этому посту");
                    _logger.Information("No need to update reaction");
                    return;
                }
                sql = $"UPDATE {nameof(Interaction)} SET {nameof(Interaction.Reaction)} = @Reaction WHERE {nameof(Interaction.Id)} = @Id;";
                await connection.ExecuteAsync(sql, new { Reaction = newReaction, interaction.Id });
                interaction.Reaction = newReaction;
            }
            else
            {
                sql = $"INSERT INTO {nameof(Interaction)} ({nameof(Interaction.ChatId)}, {nameof(Interaction.UserId)}, {nameof(Interaction.MessageId)}, {nameof(Interaction.Reaction)}, {nameof(Interaction.PosterId)}) VALUES (@ChatId, @UserId, @MessageId, @Reaction, @PosterId);";
                await connection.ExecuteAsync(sql, new { Reaction = newReaction, ChatId = msg.Chat.Id, UserId = update.CallbackQuery.From.Id, msg.MessageId, post.PosterId });
                interactions.Add(new Interaction { Reaction = newReaction });
            }

            var likes = interactions.Where(i => i.Reaction).Count();
            var dislikes = interactions.Count - likes;

            if (DateTime.UtcNow.AddMinutes(-5) > post.Timestamp && dislikes > 2 * likes + 3)
            {
                _logger.Information("Deleting post. Dislikes = {Dislikes}, Likes = {Likes}", dislikes, likes);
                await botClient.DeleteMessageAsync(msg.Chat, msg.MessageId);
                sql = $"DELETE FROM {nameof(Post)} WHERE {nameof(Post.Id)} = @Id;";
                await _dbConnection.Value.ExecuteAsync(sql, new { post.Id });
                sql = $"DELETE FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
                var deletedRows = await _dbConnection.Value.ExecuteAsync(sql, chatAndMessageIdParams);
                _logger.Debug("Deleted {Count} rows from Interaction", deletedRows);
                await botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Твой голос стал решающей каплей, этот пост удалён");
                return;
            }

            var plusText = likes > 0 ? $"{likes} 👍" : "👍";
            var minusText = dislikes > 0 ? $"{dislikes} 👎" : "👎";

            var ikm = new InlineKeyboardMarkup(new InlineKeyboardButton[]
            {
                new InlineKeyboardButton(plusText){ CallbackData = "+" },
                new InlineKeyboardButton(minusText){ CallbackData = "-" }
            });

            try
            {
                await botClient.EditMessageReplyMarkupAsync(msg.Chat, msg.MessageId, ikm);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "EditMessageReplyMarkupAsync");
            }
        }

        private static async Task HandleTextReplyAsync(Update update)
        {
            _logger.Information("New valid text message");
            var msg = update.Message;
            Debug.Assert(msg != null);
            var replyTo = msg.ReplyToMessage;
            Debug.Assert(replyTo != null);
            var from = replyTo.From;
            Debug.Assert(from != null);

            var newMessage = await botClient.SendTextMessageAsync(msg.Chat, $"{AtMentionUsername(from)}:{Environment.NewLine}{replyTo.Text}", replyMarkup: _newPostIkm);
            try
            {
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException are)
            {
                _logger.Warning(are, "Unable to delete message in HandleTextReplyAsync, duplicated update?");
            }

            if (msg.From?.Id == replyTo.From?.Id)
                await botClient.DeleteMessageAsync(msg.Chat.Id, replyTo.MessageId);

            await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.MessageId);
        }

        private static async Task InsertIntoPosts(long ChatId, long PosterId, long MessageId)
        {
            var sql = $"INSERT INTO {nameof(Post)} ({nameof(Post.ChatId)}, {nameof(Post.PosterId)}, {nameof(Post.MessageId)}, {nameof(Post.Timestamp)}) Values (@ChatId, @PosterId, @MessageId, @Timestamp);";
            await _dbConnection.Value.ExecuteAsync(sql, new { ChatId, PosterId, MessageId, Timestamp = DateTime.UtcNow });
        }

        private static async Task HandleMediaMessage(Message msg)
        {
            _logger.Information("New valid media message");
            var from = msg.From;
            Debug.Assert(from != null);
            try
            {
                var newMessage = await botClient.CopyMessageAsync(msg.Chat.Id, msg.Chat.Id, msg.MessageId, replyMarkup: _newPostIkm, caption: MentionUsername(from), parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2);
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                await InsertIntoPosts(msg.Chat.Id, from.Id, newMessage.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Cannot handle media message");
            }
        }

        private static readonly HashSet<char> _shouldBeEscaped = new() { '\\', '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        private static string MentionUsername(User user)
        {
            var whoEscaped = UserEscaped(user);
            return $"От [{whoEscaped}](tg://user?id={user.Id})";
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
                return $"От поехавшего {who} без ника в телеге";
            }
            return $"От @{user.Username}";
        }

        private static string GetFirstLastName(User user)
        {
            const string anon = "анона";
            var first = user.FirstName ?? string.Empty;
            var last = user.LastName ?? string.Empty;
            var who = $"{first} {last}".Trim();
            if (!who.Where(Char.IsAscii).Any())
                return anon;
            if (string.IsNullOrWhiteSpace(who))
                return anon;
            return who;
        }

        private static IServiceProvider CreateServices() =>
            new ServiceCollection()
                .AddFluentMigratorCore()
                .ConfigureRunner(rb => rb
                    .AddSQLite()
                    .WithGlobalConnectionString(_migrationConnectionString)
                    .ScanIn(typeof(Database.Migrations.Init).Assembly).For.Migrations())
                .AddLogging(lb => lb.AddFluentMigratorConsole())
                .BuildServiceProvider(false);

        private static void MigrateDatabase(IServiceProvider serviceProvider)
        {
            var runner = serviceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        private enum Period
        {
            Day,
            Week,
            Month
        }

        private static TimeSpan PeriodToTimeSpan(Period period) =>
            TimeSpan.FromDays(period switch
            {
                Period.Day => 1,
                Period.Week => 7,
                Period.Month => 30,
                _ => throw new ArgumentException("Enum out of range", nameof(period))
            });

        private static string ForLast(Period period) =>
            period switch
            {
                Period.Day => "последний день",
                Period.Week => "последнюю неделю",
                Period.Month => "последний месяц",
                _ => throw new ArgumentException("Enum out of range", nameof(period))
            };
    }
}
