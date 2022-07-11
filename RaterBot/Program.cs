using FluentMigrator.Runner;
using RaterBot;
using RaterBot.Database.Migrations;
using Telegram.Bot;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(
        (hostContext, services) =>
        {
            var connStr = hostContext.Configuration.GetConnectionString("Sqlite");
            services.AddHostedService<Worker>();
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
        }
    )
    .Build();

await host.RunAsync();
