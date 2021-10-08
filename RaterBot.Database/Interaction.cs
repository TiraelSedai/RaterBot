namespace RaterBot.Database
{
    public sealed class Interaction
    {
        public long Id { get; set; }
        public long ChatId { get; set; }
        public long PosterId { get; set; }
        public long MessageId { get; set; }
        public long UserId { get; set; }
        public bool Reaction { get; set; }
    }
}
