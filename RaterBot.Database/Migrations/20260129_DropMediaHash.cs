using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20260129000000)]
public class DropMediaHash : Migration
{
    public override void Up()
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
            .WithColumn(nameof(Post.ClipEmbedding))
            .AsBinary()
            .Nullable();

        Execute.Sql(
            "INSERT INTO \"New_Post\" (\"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", \"ClipEmbedding\") "
                + "SELECT \"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", \"ClipEmbedding\" FROM \"Post\""
        );

        Delete.Table(nameof(Post));
        Execute.Sql($"ALTER TABLE \"New_Post\" RENAME TO \"{nameof(Post)}\"");
    }

    public override void Down()
    {
        Alter.Table(nameof(Post)).AddColumn("MediaHash").AsString().Nullable();
    }
}
