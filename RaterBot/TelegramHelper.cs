using System.Runtime.Caching;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot;

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
    }
}
