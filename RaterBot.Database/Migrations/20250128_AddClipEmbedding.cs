using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20250128000000)]
public class AddClipEmbedding : Migration
{
    public override void Up()
    {
        Alter.Table(nameof(Post)).AddColumn("ClipEmbedding").AsBinary().Nullable();
    }

    public override void Down()
    {
        Create
            .Table("New_Post")
            .WithColumn(nameof(Post.Id))
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn(nameof(Post.ChatId))
            .AsInt64()
            .NotNullable()
            .Indexed()
            .WithColumn(nameof(Post.PosterId))
            .AsInt64()
            .NotNullable()
            .Indexed()
            .WithColumn(nameof(Post.MessageId))
            .AsInt64()
            .NotNullable()
            .Indexed()
            .WithColumn(nameof(Post.Timestamp))
            .AsDateTime()
            .NotNullable()
            .Indexed()
            .WithDefault(SystemMethods.CurrentDateTime)
            .WithColumn(nameof(Post.ReplyMessageId))
            .AsInt64()
            .Nullable()
            .WithColumn("MediaHash")
            .AsString()
            .Nullable();

        Execute.Sql(
            "INSERT INTO \"New_Post\" (\"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", \"MediaHash\") "
                + "SELECT \"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", \"MediaHash\" FROM \"Post\""
        );

        Delete.Table(nameof(Post));
        Execute.Sql($"ALTER TABLE \"New_Post\" RENAME TO \"{nameof(Post)}\"");
    }
}
