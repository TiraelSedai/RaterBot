using FluentMigrator;

namespace RaterBot.Database.Migrations;

[Migration(20230212162100)]
public sealed class TopPostsDay : Migration
{
    public override void Up()
    {
        Create
            .Table("TopPostsDay")
            .WithColumn("Id")
            .AsInt64()
            .PrimaryKey()
            .Identity()
            .WithColumn("ChatId")
            .AsInt64()
            .NotNullable()
            .Indexed()
            .WithColumn("PostId")
            .AsInt64()
            .NotNullable()
            .Indexed();
    }

    public override void Down()
    {
        Delete.Table("TopPostsDay");
    }
}
