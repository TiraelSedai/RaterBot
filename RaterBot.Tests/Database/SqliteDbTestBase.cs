using FluentMigrator.Runner;
using LinqToDB;
using LinqToDB.Async;
using LinqToDB.Data;
using LinqToDB.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using RaterBot.Database;
using RaterBot.Database.Migrations;

namespace RaterBot.Tests.Database;

public abstract class SqliteDbTestBase : IAsyncLifetime
{
    private readonly string _connectionString = "Data Source=file:memdb_" + Guid.NewGuid() + "?mode=memory&cache=shared";
    private ServiceCollection _services = null!;
    private ServiceProvider _serviceProvider = null!;

    protected SqliteDb Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _services = new ServiceCollection();
        _services.AddLinqToDBContext<SqliteDb>((_, options) => options.UseSQLite(_connectionString));
        _services.AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddSQLite().WithGlobalConnectionString(_connectionString).ScanIn(typeof(Init).Assembly).For.Migrations());

        _serviceProvider = _services.BuildServiceProvider();

        using (var scope = _serviceProvider.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
            runner.MigrateUp();
        }

        Db = _serviceProvider.GetRequiredService<SqliteDb>();
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _serviceProvider.DisposeAsync();
    }

    protected async Task<long> InsertPostAsync(long chatId, long posterId, long messageId, DateTime? timestamp = null)
    {
        var post = new Post
        {
            ChatId = chatId,
            PosterId = posterId,
            MessageId = messageId,
            Timestamp = timestamp ?? DateTime.UtcNow
        };
        return await Db.InsertWithInt64IdentityAsync(post);
    }

    protected async Task<long> InsertInteractionAsync(long userId, long postId, bool reaction)
    {
        var interaction = new Interaction
        {
            UserId = userId,
            PostId = postId,
            Reaction = reaction
        };
        return await Db.InsertWithInt64IdentityAsync(interaction);
    }
}
