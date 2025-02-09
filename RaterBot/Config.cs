using System.Collections.Frozen;

namespace RaterBot;

public class Config
{
    public Config()
    {
        var forwards = Environment.GetEnvironmentVariable("TELEGRAM_MEDIA_RATER_FORWARD");
        var map = new Dictionary<long, long>();
        if (forwards != null)
        {
            var split = forwards.Split(";");
            foreach (var pair in split)
            {
                var line = pair.Split("=");
                if (line.Length < 2)
                    continue;
                var ok1 = long.TryParse(line[0], out var from);
                var ok2 = long.TryParse(line[1], out var to);
                if (ok1 && ok2)
                    map.Add(from, to);
            }
        }

        ForwardTop = map.ToFrozenDictionary();
    }

    public readonly FrozenDictionary<long, long> ForwardTop;
}
