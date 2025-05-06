using System.Globalization;
using System.Runtime.Caching;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

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
                    var member = await telegramBotClient.GetChatMember(chat.Id, id);
                    userIdToUser[id] = member.User;
                    MemoryCache.Default.Add(id.ToString(), member, new CacheItemPolicy { SlidingExpiration = TimeSpan.FromHours(1) });
                }
                catch (ApiRequestException)
                {
                    // User not found for any reason, we don't care.
                }
            }

            return userIdToUser;
        }

        public static string LinkToMessage(Chat chat, long messageId) =>
            chat.Type == ChatType.Supergroup ? LinkToSuperGroupMessage(chat, messageId) : LinkToGroupWithNameMessage(chat, messageId);

        private static string LinkToSuperGroupMessage(Chat chat, long messageId) => $"https://t.me/c/{chat.Id.ToString()[4..]}/{messageId}";

        private static string LinkToGroupWithNameMessage(Chat chat, long messageId) =>
            chat.Username != null ? $"https://t.me/{chat.Username}/{messageId}" : "";

        public static readonly InlineKeyboardMarkup NewPostIkm = new(
            new[]
            {
                new InlineKeyboardButton("👍") { CallbackData = "+" },
                new InlineKeyboardButton("👎") { CallbackData = "-" },
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

        private static string RemoveZalgo(string input)
        {
            var normalized = input.Normalize(NormalizationForm.FormD);
            return new string([.. normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)]);
        }

        public static string UserEscaped(User user)
        {
            var who = GetFirstLastName(user);
            who = RemoveZalgo(who);
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
        [
            '\\',
            '_',
            '*',
            '[',
            ']',
            '(',
            ')',
            '~',
            '`',
            '>',
            '#',
            '+',
            '-',
            '=',
            '|',
            '{',
            '}',
            '.',
            '!',
        ];
    }
}
