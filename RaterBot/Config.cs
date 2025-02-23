using System.Collections.Frozen;
using Telegram.Bot;

namespace RaterBot;

public class Config
{
    public Config(ILogger<Config> logger, ITelegramBotClient botClient)
    {
        var forwards = Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_FORWARD");
        var map = new Dictionary<long, long>();
        if (forwards != null)
        {
            logger.LogInformation("Forward parsing {Cfg}", forwards);
            var split = forwards.Split(";");
            foreach (var pair in split)
            {
                var line = pair.Split(">");
                if (line.Length < 2)
                    continue;
                var ok1 = long.TryParse(line[0], out var from);
                var ok2 = long.TryParse(line[1], out var to);
                if (ok1 && ok2)
                {
                    logger.LogInformation("Forward config detected: from {From} to {To}", from, to);
                    map.TryAdd(from, to);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var chatFrom = await botClient.GetChat(from);
                            var chatTo = await botClient.GetChat(from);
                            logger.LogInformation("Forward config detected: from {From} to {To}", chatFrom.Title, chatTo.Title);
                        }
                        catch (Exception e)
                        {
                            logger.LogWarning(e, "Cannot get one of the forwarding chats");
                        }
                    });
                }
            }
        }

        ForwardTop = map.ToFrozenDictionary();
    }

    public readonly FrozenDictionary<long, long> ForwardTop;
}
