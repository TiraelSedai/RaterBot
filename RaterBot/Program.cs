using RaterBot;
using Telegram.Bot;

IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
        services.AddSingleton<ITelegramBotClient>(
            _ =>
                new TelegramBotClient(
                    Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API")
                        ?? throw new Exception("TELEGRAM_MEDIA_RATER_BOT_API environment variable not set")
                )
        );
    })
    .Build();

await host.RunAsync();
