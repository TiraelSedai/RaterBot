using LinqToDB.Mapping;

#pragma warning disable 1573, 1591
#nullable enable

namespace RaterBot.Database
{
    [Table("TextPosts")]
    public class TextPost
    {
        [Column("Id", IsPrimaryKey = true, IsIdentity = true, SkipOnInsert = true, SkipOnUpdate = true)]
        public long Id { get; set; }

        [Column("PostId")]
        public long PostId { get; set; }

        [Column("Text")]
        public string Text { get; set; } = string.Empty;
    }
}
