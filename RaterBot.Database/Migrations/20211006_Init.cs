using FluentMigrator;

namespace RaterBot.Database.Migrations
{
    [Migration(20211006235400)]
    public sealed class Init : Migration
    {
        public override void Up()
        {
            Create.Table(nameof(Interaction))
                .WithColumn(nameof(Interaction.Id)).AsInt64().PrimaryKey().Identity()
                .WithColumn(nameof(Interaction.ChatId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Interaction.PosterId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Interaction.MessageId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Interaction.UserId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Interaction.Reaction)).AsBoolean().NotNullable();

            Create.Table(nameof(Post))
                .WithColumn(nameof(Post.Id)).AsInt64().PrimaryKey().Identity()
                .WithColumn(nameof(Post.ChatId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Post.PosterId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Post.MessageId)).AsInt64().NotNullable().Indexed()
                .WithColumn(nameof(Post.Timestamp)).AsDateTime().NotNullable().Indexed().WithDefault(SystemMethods.CurrentDateTime);
        }

        public override void Down()
        {
            Delete.Table(nameof(Interaction));
            Delete.Table(nameof(Post));
        }
    }
}
