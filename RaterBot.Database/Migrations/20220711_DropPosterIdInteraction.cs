using FluentMigrator;

namespace RaterBot.Database.Migrations
{
    [Migration(20220711101500)]
    public sealed class DropPosterIdInteraction : Migration
    {
        public override void Up()
        {
            Create
                .Table("New_Interaciton")
                .WithColumn("Id")
                .AsInt64()
                .PrimaryKey()
                .Identity()
                .WithColumn("UserId")
                .AsInt64()
                .NotNullable()
                .Indexed()
                .WithColumn("Reaction")
                .AsBoolean()
                .NotNullable()
                .WithColumn("PostId")
                .AsInt64()
                .ForeignKey("Post", "Id")
                .NotNullable()
                .Indexed();

            Execute.Sql(
                "INSERT INTO \"New_Interaciton\" (\"UserId\", \"Reaction\", \"PostId\") "
                    + "SELECT i.\"UserId\", i.\"Reaction\", p.\"Id\" FROM \"Post\" p "
                    + "INNER JOIN \"Interaction\" i ON i.\"MessageId\" = p.\"MessageId\" AND i.\"ChatId\" = p.\"ChatId\""
            );

            Delete.Table(nameof(Interaction));
            Execute.Sql($"ALTER TABLE \"New_Interaciton\" RENAME TO \"{nameof(Interaction)}\"");
        }

        public override void Down()
        {
            Create
                .Table("Old_Interaction")
                .WithColumn(nameof(Interaction.Id))
                .AsInt64()
                .PrimaryKey()
                .Identity()
                .WithColumn("ChatId")
                .AsInt64()
                .NotNullable()
                .Indexed()
                .WithColumn("PosterId")
                .AsInt64()
                .NotNullable()
                .Indexed()
                .WithColumn("MessageId")
                .AsInt64()
                .NotNullable()
                .Indexed()
                .WithColumn(nameof(Interaction.UserId))
                .AsInt64()
                .NotNullable()
                .Indexed()
                .WithColumn(nameof(Interaction.Reaction))
                .AsBoolean()
                .NotNullable();

            Execute.Sql(
                "INSERT INTO \"Old_Interaction\" (\"UserId\", \"Reaction\", \"ChatId\", \"PosterId\", \"MessageId\") "
                    + "SELECT i.\"UserId\", i.\"Reaction\", p.\"ChatId\", p.\"PosterId\", p.\"MessageId\" FROM \"Post\" p "
                    + "INNER JOIN \"Interaction\" i ON i.\"MessageId\" = p.\"MessageId\" AND i.\"ChatId\" = p.\"ChatId\""
            );

            Delete.Table(nameof(Interaction));
            Execute.Sql($"ALTER TABLE \"Old_Interaction\" RENAME TO \"{nameof(Interaction)}\"");
        }
    }
}
