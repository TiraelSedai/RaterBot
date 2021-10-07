using System;

namespace RaterBot.Database
{
    public class Post
    {
        public long Id { get; set; }
        public long ChatId { get; set; }
        public long PosterId { get; set; }
        public long MessageId { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
