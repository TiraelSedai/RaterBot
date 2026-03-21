using FluentMigrator.Runner;
using LinqToDB;
using LinqToDB.Data;
using LinqToDB.Extensions.DependencyInjection;
using LinqToDB.Extensions.Logging;
using RaterBot;
using RaterBot.Database;
using RaterBot.Database.Migrations;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System.Runtime;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Sqlite") ?? throw new ArgumentNullException("Sqlite config section is null");

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<TopPostDayService>();
builder.Services.AddScoped<MessageHandler>();
builder.Services.AddSingleton<IMediaDownloader, ProcessMediaDownloader>();
builder.Services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(
    Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_BOT_API")
        ?? throw new Exception("TELEGRAM_MEDIA_RATER_BOT_API environment variable not set")
));
builder.Services.AddSingleton<Config>();
builder.Services.AddSingleton<RaterBot.Polly>();
builder.Services.AddSingleton<VectorSearchService>();
builder.Services.AddSingleton<IVectorSearchService>(provider => provider.GetRequiredService<VectorSearchService>());
builder.Services.AddSerilog(
    (services, configuration) =>
        configuration
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("LinqToDB.Data.DataConnection", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(new CompactJsonFormatter())
);
builder.Services
    .AddFluentMigratorCore()
    .ConfigureRunner(rb => rb.AddSQLite().WithGlobalConnectionString(connStr).ScanIn(typeof(Init).Assembly).For.Migrations())
    .AddLogging(lb => lb.AddFluentMigratorConsole())
    .BuildServiceProvider(false);
builder.Services.AddLinqToDBContext<SqliteDb>((provider, options) => options.UseSQLite(connStr).UseDefaultLogging(provider));

var host = builder.Build();

var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
startupLogger.LogInformation(
    "GC mode: {GcMode}. IsServerGC: {IsServerGC}. DOTNET_gcServer: {DotnetGcServer}. COMPlus_gcServer: {ComPlusGcServer}",
    GCSettings.IsServerGC ? "Server" : "Workstation",
    GCSettings.IsServerGC,
    Environment.GetEnvironmentVariable("DOTNET_gcServer"),
    Environment.GetEnvironmentVariable("COMPlus_gcServer")
);

using (var scope = host.Services.CreateScope())
{
    if (!Directory.Exists("db"))
        Directory.CreateDirectory("db");
    if (!File.Exists("db/cookies.txt"))
        File.Create("db/cookies.txt");

    using (var dbc = scope.ServiceProvider.GetRequiredService<SqliteDb>())
    {
        dbc.Execute("PRAGMA journal_mode = WAL;");
        dbc.Execute("PRAGMA foreign_keys = ON;");
        dbc.Execute("PRAGMA synchronous = NORMAL;");
        dbc.Execute("PRAGMA temp_store = memory;");
        dbc.Execute("PRAGMA busy_timeout = 5000;");
        dbc.Execute("PRAGMA cache_size = -64000;");
        dbc.Execute("PRAGMA mmap_size = 268435456;");
        try
        {
            dbc.Execute("UPDATE \"VersionInfo\" SET \"Version\" = 20240629000000 WHERE \"Version\" = 20240629;");
            dbc.Execute("UPDATE \"VersionInfo\" SET \"Version\" = 20231203822340 WHERE \"Version\" = 202312038223400;");
        }
        catch { }
    }

    var migrationRunner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
    migrationRunner.MigrateUp();
}

await host.RunAsync();
