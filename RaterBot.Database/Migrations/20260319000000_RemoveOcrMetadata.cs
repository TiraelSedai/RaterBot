using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20260319000000)]
public class RemoveOcrMetadata : Migration
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
            .WithColumn(nameof(Post.PosterId))
            .AsInt64()
            .NotNullable()
            .WithColumn(nameof(Post.MessageId))
            .AsInt64()
            .NotNullable()
            .WithColumn(nameof(Post.Timestamp))
            .AsDateTime()
            .NotNullable()
            .WithDefault(SystemMethods.CurrentDateTime)
            .WithColumn(nameof(Post.ReplyMessageId))
            .AsInt64()
            .Nullable()
            .WithColumn(nameof(Post.ClipEmbedding))
            .AsBinary()
            .Nullable()
            .WithColumn(nameof(Post.ClipEmbedding2))
            .AsBinary()
            .Nullable()
            .WithColumn(nameof(Post.ClipEmbedding3))
            .AsBinary()
            .Nullable()
            .WithColumn(nameof(Post.ClipEmbedding4))
            .AsBinary()
            .Nullable()
            .WithColumn(nameof(Post.ClipEmbedding5))
            .AsBinary()
            .Nullable()
            .WithColumn(nameof(Post.VectorMediaKind))
            .AsInt32()
            .Nullable();

        Execute.Sql(
            "INSERT INTO \"New_Post\" (\"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", "
                + "\"ClipEmbedding\", \"ClipEmbedding2\", \"ClipEmbedding3\", \"ClipEmbedding4\", \"ClipEmbedding5\", \"VectorMediaKind\") "
                + "SELECT \"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", "
                + "\"ClipEmbedding\", \"ClipEmbedding2\", \"ClipEmbedding3\", \"ClipEmbedding4\", \"ClipEmbedding5\", \"VectorMediaKind\" FROM \"Post\""
        );

        Delete.Table(nameof(Post));
        Execute.Sql($"ALTER TABLE \"New_Post\" RENAME TO \"{nameof(Post)}\"");

        Create.Index().OnTable(nameof(Post)).OnColumn(nameof(Post.ChatId)).Ascending();
        Create.Index().OnTable(nameof(Post)).OnColumn(nameof(Post.PosterId)).Ascending();
        Create.Index().OnTable(nameof(Post)).OnColumn(nameof(Post.MessageId)).Ascending();
        Create.Index().OnTable(nameof(Post)).OnColumn(nameof(Post.Timestamp)).Ascending();
    }

    public override void Down()
    {
        Alter.Table(nameof(Post)).AddColumn("TextCoverageRatio").AsDouble().Nullable();
        Alter.Table(nameof(Post)).AddColumn("OcrTextNormalized").AsString().Nullable();
        Alter.Table(nameof(Post)).AddColumn("OcrAvgConfidence").AsDouble().Nullable();
        Alter.Table(nameof(Post)).AddColumn("IsTextHeavy").AsBoolean().Nullable();
    }
}
