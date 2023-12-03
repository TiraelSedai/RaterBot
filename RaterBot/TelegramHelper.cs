using System.Runtime.Caching;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;
using System.Text;

namespace RaterBot
{
    internal class TelegramHelper
    {
        public static async Task<Dictionary<long, User>> GetTelegramUsers(
            Chat chat,
            ICollection<long> userIds,
            ITelegramBotClient telegramBotClient
        )
        {
            var userIdToUser = new Dictionary<long, User>(userIds.Count);
            foreach (var id in userIds)
            {
                if (MemoryCache.Default.Get(id.ToString()) is User fromCache)
                {
                    userIdToUser[id] = fromCache;
                    continue;
                }

                try
                {
                    var member = await telegramBotClient.GetChatMemberAsync(chat.Id, id);
                    userIdToUser[id] = member.User;
                    MemoryCache.Default.Add(
                        id.ToString(),
                        member,
                        new CacheItemPolicy { SlidingExpiration = TimeSpan.FromHours(1) }
                    );
                }
                catch (ApiRequestException)
                {
                    // User not found for any reason, we don't care.
                }
            }

            return userIdToUser;
        }

        public static readonly InlineKeyboardMarkup NewPostIkm =
            new(
                new[]
                {
                    new InlineKeyboardButton("👍") { CallbackData = "+" },
                    new InlineKeyboardButton("👎") { CallbackData = "-" }
                }
            );

        public static string MentionUsername(User user)
        {
            var whoEscaped = UserEscaped(user);
            return $"[От {whoEscaped}](tg://user?id={user.Id})";
        }

        public static string GetFirstLastName(User user)
        {
            var last = user.LastName ?? string.Empty;
            var who = $"{user.FirstName} {last}".Trim();
            if (string.IsNullOrWhiteSpace(who))
                who = "аноним";
            return who;
        }

        public static string UserEscaped(User user)
        {
            var who = GetFirstLastName(user);
            var whoEscaped = new StringBuilder(who.Length);
            foreach (var c in who)
            {
                if (_shouldBeEscaped.Contains(c))
                    whoEscaped.Append('\\');
                whoEscaped.Append(c);
            }

            return whoEscaped.ToString();
        }

        private static readonly char[] _shouldBeEscaped =
            [ '\\', '_', '*', '[', ']', '(', ')', '~', '`', '>', '#', '+', '-', '=', '|', '{', '}', '.', '!' ];
    }
}
