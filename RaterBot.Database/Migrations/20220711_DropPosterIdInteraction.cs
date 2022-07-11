using FluentMigrator;

namespace RaterBot.Database.Migrations
{
    [Migration(20220711101500)]
    public sealed class DropPosterIdInteraction : Migration
    {
        public override void Up()
        {
            Execute.Sql("DROP INDEX \"IX_Interaction_PosterId\";");
            Execute.Sql("ALTER TABLE \"Interaction\" DROP COLUMN \"PosterId\";");
        }

        public override void Down()
        {
            Alter.Table(nameof(Interaction)).AddColumn("PosterId").AsInt64().NotNullable().Indexed();
        }
    }
}
