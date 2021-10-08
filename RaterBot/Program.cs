﻿using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using Serilog;
using Serilog.Core;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace RaterBot
{
    internal sealed class Program
    {
        private static readonly Logger _logger = new LoggerConfiguration().WriteTo.Console().CreateLogger();

        static readonly ITelegramBotClient botClient = new TelegramBotClient(Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API"));
        const int updateLimit = 100;
        const int timeout = 120;
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
            new InlineKeyboardButton{ CallbackData = "+", Text = "👍" },
            new InlineKeyboardButton{ CallbackData = "-", Text = "👎" },
        });

        private static void InitAndMigrateDb()
        {
            SQLitePCL.Batteries.Init();

            var serviceProvider = CreateServices();
            using var scope = serviceProvider.CreateScope();
            MigrateDatabase(scope.ServiceProvider);
        }

        static async Task Main(string[] args)
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
                        {
                            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.CallbackQuery)
                                await HandleCallbackData(update);

                            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message)
                            {
                                var msg = update.Message;
                                if (msg.Text == "/top_posts_week@mediarater_bot" || msg.Text == "/top_posts_week")
                                {
                                    await HandleTopWeekPosts(update);
                                    continue;
                                }

                                if (msg.ReplyToMessage != null)
                                {
                                    if (msg.Text == "/text@mediarater_bot" || msg.Text == "/text")
                                    {
                                        if (msg.ReplyToMessage.From.Id == me.Id)
                                        {
                                            await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку не от бота");
                                            continue;
                                        }
                                        if (string.IsNullOrWhiteSpace(msg.ReplyToMessage.Text))
                                        {
                                            await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                                            continue;
                                        }
                                        await HandleTextReplyAsync(update);
                                    }
                                    continue;
                                }
                                else
                                {
                                    if (msg.Text == "/text@mediarater_bot" || msg.Text == "/text")
                                    {
                                        await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                                        continue;
                                    }
                                }

                                if (msg.Type == Telegram.Bot.Types.Enums.MessageType.Photo
                                    || msg.Type == Telegram.Bot.Types.Enums.MessageType.Video
                                    || (msg.Type == Telegram.Bot.Types.Enums.MessageType.Document
                                        && (msg.Document.MimeType.StartsWith("image") || msg.Document.MimeType.StartsWith("video"))))
                                {
                                    await HandleMediaMessage(msg);
                                }
                            }
                        }

                        offset = updates.Max(u => u.Id) + 1;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "General update exception");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }

        private static async Task HandleTopWeekPosts(Update update)
        {
            var chat = update.Message.Chat;

            if (chat.Type != Telegram.Bot.Types.Enums.ChatType.Supergroup && string.IsNullOrWhiteSpace(chat.Username))
            {
                await botClient.SendTextMessageAsync(chat, "Этот чат не является супергруппой и не имеет имени: нет возможности оставлять ссылки на посты");
                _logger.Information($"{nameof(HandleTopWeekPosts)} - unable to link top posts, skipping");
                return;
            }

            var sql = $"SELECT {nameof(Interaction)}.{nameof(Interaction.MessageId)}, COUNT(*), {nameof(Interaction)}.{nameof(Interaction.PosterId)}" +
                $" FROM {nameof(Post)} INNER JOIN {nameof(Interaction)} ON {nameof(Post)}.{nameof(MessageId)} = {nameof(Interaction)}.{nameof(Interaction.MessageId)}" +
                $" WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.Timestamp)} > @WeekAgo AND {nameof(Interaction)}.{nameof(Interaction.Reaction)} = true" +
                $" GROUP BY {nameof(Interaction)}.{nameof(Interaction.MessageId)};";
            var sqlParams = new { WeekAgo = DateTime.UtcNow - TimeSpan.FromDays(7), ChatId = chat.Id };
            var plusQuery = await _dbConnection.Value.QueryAsync<(long MessageId, long PlusCount, long PosterId)>(sql, sqlParams);
            var plus = plusQuery.ToDictionary(x => x.MessageId, x => x.PlusCount);
            var messageIdToUserId = plusQuery.ToDictionary(x => x.MessageId, x => x.PosterId);
            if (!plus.Any())
            {
                await botClient.SendTextMessageAsync(chat, "Не найдено заплюсованных постов за последнюю неделю");
                _logger.Information($"{nameof(HandleTopWeekPosts)} - no upvoted posts, skipping");
                return;
            }
            sql = $"SELECT {nameof(Interaction)}.{nameof(Interaction.MessageId)}, COUNT(*)" +
                $" FROM {nameof(Post)} INNER JOIN {nameof(Interaction)} ON {nameof(Post)}.{nameof(MessageId)} = {nameof(Interaction)}.{nameof(Interaction.MessageId)}" +
                $" WHERE {nameof(Post)}.{nameof(Post.ChatId)} = @ChatId AND {nameof(Post)}.{nameof(Post.Timestamp)} > @WeekAgo AND {nameof(Interaction)}.{nameof(Interaction.Reaction)} = false" +
                $" GROUP BY {nameof(Interaction)}.{nameof(Interaction.MessageId)};";
            var minus = (await _dbConnection.Value.QueryAsync<(long MessageId, long MinusCount)>(sql, sqlParams)).ToDictionary(x => x.MessageId, y => y.MinusCount);

            var keys = plus.Keys.ToList();
            foreach (var key in keys)
                plus[key] -= minus.GetValueOrDefault(key);
            var topTen = plus.OrderByDescending(x => x.Value).Take(10);

            var topTenWithUsers = topTen.Select(x => x.Key).ToDictionary(x => x, x => new User());

            var userIds = topTen.Select(x => messageIdToUserId[x.Key]).Distinct().ToList();
            var userIdToUser = new Dictionary<long, User>(userIds.Count);
            foreach (var id in userIds)
            {
                var member = await botClient.GetChatMemberAsync(chat, id);
                userIdToUser.Add(id, member.User);
            }

            var message = new StringBuilder(1024);
            var i = 0;
            var sg = chat.Type == Telegram.Bot.Types.Enums.ChatType.Supergroup;
            foreach (var item in topTen)
            {
                switch (i)
                {
                    case 0:
                        message.Append("🥇 ");
                        break;
                    case 1:
                        message.Append($"{Environment.NewLine}🥈 ");
                        break;
                    case 2:
                        message.Append($"{Environment.NewLine}🥉 ");
                        break;
                    default:
                        message.Append($"{Environment.NewLine}{i+1} ");
                        break;
                }

                var user = userIdToUser[messageIdToUserId[item.Key]];

                message.Append("От ");
                message.Append($"[{UserEscaped(user)}](");

                var link = sg ? LinkToSuperGroupMessage(chat, item.Key) : LinkToGroupWithNameMessage(chat, item.Key);
                message.Append(link);
                message.Append(") ");
                if (item.Value > 0)
                    message.Append("\\+");
                message.Append(item.Value);
                i++;
            }

            await botClient.SendTextMessageAsync(chat, message.ToString(), Telegram.Bot.Types.Enums.ParseMode.MarkdownV2);
        }

        private static string LinkToSuperGroupMessage(ChatId chatId, long messageId)
            => $"https://t.me/c/{chatId.Identifier.ToString()[4..]}/{messageId}";

        private static string LinkToGroupWithNameMessage(Chat chat, long messageId)
            => $"https://t.me/{chat.Username}/{messageId}";

        private static async Task HandleCallbackData(Update update)
        {
            var msg = update.CallbackQuery.Message;
            var rm = msg.ReplyMarkup;
            var firstRow = rm.InlineKeyboard.First();
            var connection = _dbConnection.Value;
            switch (update.CallbackQuery.Data)
            {
                case "+":
                case "-":
                    _logger.Debug("Valid callback request");
                    var sql = $"SELECT * FROM {nameof(Post)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
                    var post = await connection.QuerySingleOrDefaultAsync<Post>(sql, new { ChatId = msg.Chat.Id, msg.MessageId });
                    if (post == null)
                    {
                        _logger.Error("Cannot find post in the database, ChatId = {ChatId}, MessageId = {MessageId}", msg.Chat.Id, msg.MessageId);
                        return;
                    }

                    if (post.PosterId == update.CallbackQuery.From.Id)
                    {
                        await botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, "Нельзя голосовать за свои посты!");
                        return;
                    }

                    sql = $"SELECT * FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} = @MessageId;";
                    var interactions = (await connection.QueryAsync<Interaction>(sql, new { ChatId = msg.Chat.Id, msg.MessageId })).ToList();
                    var interaction = interactions.SingleOrDefault(i => i.UserId == update.CallbackQuery.From.Id);

                    if (interaction != null)
                    {
                        var newReaction = update.CallbackQuery.Data == "+";
                        if (newReaction == interaction.Reaction)
                        {
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
                        await connection.ExecuteAsync(sql, new { Reaction = update.CallbackQuery.Data == "+", ChatId = msg.Chat.Id, UserId = update.CallbackQuery.From.Id, msg.MessageId, post.PosterId });
                        interactions.Add(new Interaction { Reaction = update.CallbackQuery.Data == "+" });
                    }

                    var likes = interactions.Where(i => i.Reaction).Count();
                    var dislikes = interactions.Count - likes;
                    var plusText = likes > 0 ? $"{likes} 👍" : "👍";
                    var minusText = dislikes > 0 ? $"{dislikes} 👎" : "👎";

                    var ikm = new InlineKeyboardMarkup(new InlineKeyboardButton[]
                    {
                        new InlineKeyboardButton{ CallbackData = "+", Text = plusText },
                        new InlineKeyboardButton{ CallbackData = "-", Text = minusText },
                    });

                    var chat = new ChatId(update.CallbackQuery.Message.Chat.Id);
                    try
                    {
                        await botClient.EditMessageReplyMarkupAsync(chat, update.CallbackQuery.Message.MessageId, ikm);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, nameof(ITelegramBotClient.EditMessageReplyMarkupAsync));
                    }
                    break;
                default:
                    _logger.Warning("Invalid callback query data");
                    break;
            }
        }

        private static async Task HandleTextReplyAsync(Update update)
        {
            _logger.Information("New valid text message");
            var msg = update.Message;
            var replyTo = msg.ReplyToMessage;

            var from = replyTo.From;

            var newMessage = await botClient.SendTextMessageAsync(msg.Chat, $"{AtMentionUsername(from)}:{Environment.NewLine}{replyTo.Text}", replyMarkup: _newPostIkm);
            try
            {
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException are)
            {
                _logger.Warning(are, "Unable to delete message in HandleTextReplyAsync, duplicated update?");
            }

            if (msg.From.Id == replyTo.From.Id)
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

        private static readonly HashSet<char> _shouldBeEscaped = new() { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        private static string MentionUsername(User user)
        {
            var whoEscaped = UserEscaped(user);
            return $"От [{whoEscaped}](tg://user?id={user.Id})";
        }

        private static string UserEscaped(User user)
        {
            var first = user.FirstName ?? string.Empty;
            var last = user.LastName ?? string.Empty;
            var who = $"{first} {last}".Trim();
            if (string.IsNullOrWhiteSpace(who))
                who = "анонима";

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
                var first = user.FirstName ?? string.Empty;
                var last = user.LastName ?? string.Empty;
                var who = $"{first} {last}".Trim();
                if (who.Length == 0)
                    who = "анонима";

                return $"поехавшего {who} без ника в телеге";
            }

            return $"От @{user.Username}";
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
    }
}