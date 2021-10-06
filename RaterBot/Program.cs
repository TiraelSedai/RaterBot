using Dapper;
using FluentMigrator.Runner;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using Serilog;
using Serilog.Core;
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
            var msg = update.Message;
            var replyTo = msg.ReplyToMessage;

            var from = replyTo.From;

            var newMessage = await botClient.SendTextMessageAsync(msg.Chat, $"От @{from.Username}:{Environment.NewLine}{replyTo.Text}", replyMarkup: _newPostIkm);
            try
            {
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException)
            {
                // its fine, duplicate update?
            }

            if (msg.From.Id == replyTo.From.Id)
                await botClient.DeleteMessageAsync(msg.Chat.Id, replyTo.MessageId);

            var sql = $"INSERT INTO {nameof(Post)} ({nameof(Post.ChatId)}, {nameof(Post.PosterId)}, {nameof(Post.MessageId)}) Values (@ChatId, @PosterId, @MessageId);";
            await _dbConnection.Value.ExecuteAsync(sql, new { ChatId = msg.Chat.Id, PosterId = from.Id, newMessage.MessageId });
        }

        private static async Task HandleMediaMessage(Message msg)
        {
            _logger.Debug("Valid media message");

            var from = msg.From;
            try
            {
                var who = GetFirstLast(from);

                var newMessage = await botClient.CopyMessageAsync(msg.Chat.Id, msg.Chat.Id, msg.MessageId, replyMarkup: _newPostIkm, caption: $"От [{who}](https://t.me/{from.Username})", parseMode: Telegram.Bot.Types.Enums.ParseMode.MarkdownV2);
                await botClient.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);

                var sql = $"INSERT INTO {nameof(Post)} ({nameof(Post.ChatId)}, {nameof(Post.PosterId)}, {nameof(Post.MessageId)}) Values (@ChatId, @PosterId, @MessageId);";
                await _dbConnection.Value.ExecuteAsync(sql, new { ChatId = msg.Chat.Id, PosterId = from.Id, MessageId = newMessage.Id });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Cannot handle media message");
            }
        }

        private static string GetFirstLast(User from)
        {
            var first = from.FirstName ?? string.Empty;
            var second = from.LastName ?? string.Empty;
            var who = $"{first} {second}".Trim();
            if (who.Length == 0)
                who = "анонима";
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
    }
}
