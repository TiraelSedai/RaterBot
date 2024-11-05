using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace RaterBot;

internal static class TelegramHelperExtensions
{
    public static void ReplyAndDeleteLater(
        this ITelegramBotClient bot,
        Message message,
        string text,
        ILogger? logger = null,
        ParseMode parseMode = ParseMode.None
    )
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var m = await bot.SendMessage(
                    message.Chat.Id,
                    text,
                    replyParameters: message,
                    disableNotification: true,
                    parseMode: parseMode
                );
                await Task.Delay(TimeSpan.FromMinutes(10));
                await bot.DeleteMessage(message.Chat.Id, m.MessageId);
            }
            catch (ApiRequestException are)
            {
                logger?.LogError(are, "Unable to send {Text} with parseMode = {ParseMode}", text, parseMode);
            }
            await bot.DeleteMessage(message.Chat.Id, message.MessageId);
        });
    }

    public static void TemporaryReply(
        this ITelegramBotClient bot,
        ChatId chatId,
        MessageId messageId,
        string text,
        ILogger? logger = null,
        ParseMode parseMode = ParseMode.None
    )
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var m = await bot.SendMessage(chatId, text, replyParameters: messageId.Id, disableNotification: true, parseMode: parseMode);
                await Task.Delay(TimeSpan.FromMinutes(30));
                await bot.DeleteMessage(chatId, m.MessageId);
            }
            catch (ApiRequestException are)
            {
                logger?.LogError(are, "Unable to send {Text} with parseMode = {ParseMode}", text, parseMode);
            }
        });
    }
}
