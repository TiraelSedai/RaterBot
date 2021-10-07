using Dapper;
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
    class Program
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
                            await HandleUpdate(me, update);
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

        private static async Task HandleUpdate(User me, Update update)
        {
            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.CallbackQuery)
                await HandleCallbackData(update);

            if (update.Type == Telegram.Bot.Types.Enums.UpdateType.Message)
            {
                var msg = update.Message;
                if (msg.ReplyToMessage != null)
                {
                    if (msg.Text == "/text@mediarater_bot" || msg.Text == "/text")
                    {
                        if (msg.ReplyToMessage.From.Id == me.Id)
                        {
                            await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку не от бота");
                            return;
                        }
                        if (string.IsNullOrWhiteSpace(msg.ReplyToMessage.Text))
                        {
                            await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                            return;
                        }
                        await HandleTextReplyAsync(update);
                    }
                    return;
                }
                else
                {
                    if (msg.Text == "/text@mediarater_bot" || msg.Text == "/text")
                    {
                        await botClient.SendTextMessageAsync(msg.Chat, "Эту команду нужно вызывать реплаем на текстовое сообщение или ссылку");
                        return;
                    }
                }

                //if (msg.Text == "/top_posts_week@mediarater_bot" || msg.Text == "/top_posts_week")
                //{
                //    await HandleTopPosts(update);
                //}

                if (msg.Type == Telegram.Bot.Types.Enums.MessageType.Photo
                    || msg.Type == Telegram.Bot.Types.Enums.MessageType.Video
                    || (msg.Type == Telegram.Bot.Types.Enums.MessageType.Document
                        && (msg.Document.MimeType.StartsWith("image") || msg.Document.MimeType.StartsWith("video"))))
                {
                    await HandleMediaMessage(msg);
                }
            }
        }

        //private static async Task HandleTopPosts(Update update)
        //{
        //    _logger.Debug("HandleTopPosts");

        //    var sql = $"SELECT {nameof(Post.MessageId)} FROM {nameof(Post)} WHERE {nameof(Post.ChatId)} = @ChatId AND {nameof(Post.Timestamp)} > @WeekAgo;";
        //    var postsIds = (await _dbConnection.Value.QueryAsync<long>(sql, new { ChatId = update.Message.Chat.Id, WeekAgo = DateTime.Now })).ToList();
        //    if (!postsIds.Any())
        //        return;


        //    update.Message.

        //    var postCount = postsIds.Count;
        //    sql = $"SELECT * FROM {nameof(Interaction)} WHERE {nameof(Interaction.ChatId)} = @ChatId AND {nameof(Interaction.MessageId)} IN @PostIdList;";

        //    //var post = await connection.QuerySingleOrDefaultAsync<Post>(sql, new { ChatId = msg.Chat.Id, msg.MessageId });
        //}

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
                    var sql = $"SELECT * FROM {nameof(Post)} WHERE {nameof(Post.ChatId)} = @ChatId AND {nameof(Post.MessageId)} = @MessageId;";
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
            var msg = update.Message;
            var replyTo = msg.ReplyToMessage;

            var from = replyTo.From;

            var newMessage = await botClient.SendTextMessageAsync(msg.Chat, $"От {MentionUsername(from)}:{Environment.NewLine}{replyTo.Text}", replyMarkup: _newPostIkm);
            try
            {
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException are)
            {
                _logger.Warning(are, "HandleTextReplyAsync cannot delete message - duplicate update?");
            }

            if (msg.From.Id == replyTo.From.Id)
                await botClient.DeleteMessageAsync(msg.Chat.Id, replyTo.MessageId);
            await InsertIntoPost(msg.Chat.Id, from.Id, newMessage.MessageId);
        }

        private static async Task InsertIntoPost(long chatId, long posterId, long messageId)
        {
            var sql = $"INSERT INTO {nameof(Post)} ({nameof(Post.ChatId)}, {nameof(Post.PosterId)}, {nameof(Post.MessageId)}, {nameof(Post.Timestamp)}) Values (@ChatId, @PosterId, @MessageId, @Timestamp);";
            await _dbConnection.Value.ExecuteAsync(sql, new { ChatId = chatId, PosterId = posterId, messageId, Timestamp = DateTime.UtcNow });
        }

        private static async Task HandleMediaMessage(Message msg)
        {
            _logger.Debug("Valid media message");

            var from = msg.From;
            try
            {
                var newMessage = await botClient.CopyMessageAsync(msg.Chat.Id, msg.Chat.Id, msg.MessageId, replyMarkup: _newPostIkm, caption: MentionUsername(from), parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2);
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                await InsertIntoPost(msg.Chat.Id, from.Id, newMessage.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Cannot handle media message");
            }
        }

        private static readonly HashSet<char> _shouldBeEscaped = new() { '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' };

        private static string MentionUsername(User user)
        {
            var first = user.FirstName ?? string.Empty;
            var last = user.LastName ?? string.Empty;
            var who = $"{first} {last}".Trim();
            if (string.IsNullOrWhiteSpace(who))
                who = "анонима";

            var whoEscaped = new StringBuilder(who.Length);
            foreach(var c in who)
            {
                if (_shouldBeEscaped.Contains(c))
                    whoEscaped.Append('\\');
                whoEscaped.Append(c);
            }

            return $"От [{whoEscaped}](tg://user?id={user.Id})";
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
