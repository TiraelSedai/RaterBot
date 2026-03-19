using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20260218010000)]
public class AddMotionEmbeddingsAndMediaKind : Migration
{
    public override void Up()
    {
        Alter.Table(nameof(Post)).AddColumn(nameof(Post.ClipEmbedding2)).AsBinary().Nullable();
        Alter.Table(nameof(Post)).AddColumn(nameof(Post.ClipEmbedding3)).AsBinary().Nullable();
        Alter.Table(nameof(Post)).AddColumn(nameof(Post.ClipEmbedding4)).AsBinary().Nullable();
        Alter.Table(nameof(Post)).AddColumn(nameof(Post.ClipEmbedding5)).AsBinary().Nullable();
        Alter.Table(nameof(Post)).AddColumn(nameof(Post.VectorMediaKind)).AsInt32().Nullable();
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
            .WithColumn(nameof(Post.ClipEmbedding))
            .AsBinary()
            .Nullable()
            .WithColumn("TextCoverageRatio")
            .AsDouble()
            .Nullable()
            .WithColumn("OcrTextNormalized")
            .AsString()
            .Nullable()
            .WithColumn("OcrAvgConfidence")
            .AsDouble()
            .Nullable()
            .WithColumn("IsTextHeavy")
            .AsBoolean()
            .Nullable();

        Execute.Sql(
            "INSERT INTO \"New_Post\" (\"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", "
                + "\"ClipEmbedding\", \"TextCoverageRatio\", \"OcrTextNormalized\", \"OcrAvgConfidence\", \"IsTextHeavy\") "
                + "SELECT \"Id\", \"ChatId\", \"PosterId\", \"MessageId\", \"Timestamp\", \"ReplyMessageId\", "
                + "\"ClipEmbedding\", \"TextCoverageRatio\", \"OcrTextNormalized\", \"OcrAvgConfidence\", \"IsTextHeavy\" FROM \"Post\""
        );

        Delete.Table(nameof(Post));
        Execute.Sql($"ALTER TABLE \"New_Post\" RENAME TO \"{nameof(Post)}\"");
    }
}
