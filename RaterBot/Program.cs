using FluentMigrator.Runner;
using LinqToDB;
using LinqToDB.AspNet;
using LinqToDB.AspNet.Logging;
using LinqToDB.Configuration;
using LinqToDB.Data;
using RaterBot;
using RaterBot.Database;
using RaterBot.Database.Migrations;
using Telegram.Bot;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(
        (hostContext, services) =>
        {
            var connStr =
                hostContext.Configuration.GetConnectionString("Sqlite")
                ?? throw new ArgumentNullException("Sqlite config section is null");
            services.AddHostedService<Worker>();
            services.AddScoped<MessageHandler>();
            services.AddSingleton<ITelegramBotClient>(
                _ =>
                    new TelegramBotClient(
                        Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API")
                            ?? throw new Exception("TELEGRAM_MEDIA_RATER_BOT_API environment variable not set")
                    )
            );
            services
                .AddFluentMigratorCore()
                .ConfigureRunner(
                    rb =>
                        rb.AddSQLite()
                            .WithGlobalConnectionString(connStr)
                            .ScanIn(typeof(Init).Assembly)
                            .For.Migrations()
                )
                .AddLogging(lb => lb.AddFluentMigratorConsole())
                .BuildServiceProvider(false);
            services.AddLinqToDBContext<SqliteDb>(
                (provider, options) => options.UseSQLite(connStr).UseDefaultLogging(provider)
            );
        }
    )
    .Build();

using (var scope = host.Services.CreateScope())
{
    if (!Directory.Exists("db"))
        Directory.CreateDirectory("db");
    if (!File.Exists("db/cookies.txt"))
        File.Create("db/cookies.txt");

    using (var dbc = scope.ServiceProvider.GetRequiredService<SqliteDb>())
    {
        dbc.Execute("PRAGMA foreign_keys = ON;");
        dbc.Execute("PRAGMA journal_mode = WAL;");
        dbc.Execute("PRAGMA synchronous = NORMAL;");
        dbc.Execute("PRAGMA temp_store = memory;");
        dbc.Execute("VACUUM;");
    }

    var migrationRunner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    migrationRunner.MigrateUp();
}

await host.RunAsync();
