using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20260422000000)]
public sealed class AddTextPosts : Migration
{
    public override void Up()
    {
        Create
            .Table("TextPosts")
            .WithColumn("Id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("PostId")
            .AsInt64()
            .NotNullable()
            .Indexed()
            .WithColumn("Text")
            .AsString()
            .NotNullable();
    }

    public override void Down()
    {
        Delete.Table("TextPosts");
    }
}
