using Polly;
using Polly.Fallback;
using Polly.Retry;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace RaterBot;

internal class Polly
{
    public ResiliencePipeline<Message> MessageEdit { get; }
    private ResiliencePipeline RetryAfter { get; }

    public Polly(ILogger<Polly> logger)
    {
        var fbOption = new FallbackStrategyOptions<Message>()
        {
            ShouldHandle = static args =>
                args.Outcome switch
                {
                    { Exception: ApiRequestException are }
                        when are.Message.Contains(
                            "message is not modified: specified new message content and reply markup are exactly the same as a current content and reply markup of the message"
                        ) => PredicateResult.True(),
                    _ => PredicateResult.False(),
                },
            FallbackAction = static _ => Outcome.FromResultAsValueTask(new Message()),
            OnFallback = _ =>
            {
                logger.LogInformation("Message with reply markup: not modified, already the same");
                return ValueTask.CompletedTask;
            },
        };

        var options = new RetryStrategyOptions()
        {
            ShouldHandle = new PredicateBuilder().Handle<ApiRequestException>(e => e.Parameters?.RetryAfter != null),
            MaxRetryAttempts = 3,
            DelayGenerator = args =>
            {
                var exception = args.Outcome.Exception as ApiRequestException;
                var retryAfter = exception?.Parameters?.RetryAfter;
                if (retryAfter != null)
                    return ValueTask.FromResult<TimeSpan?>(TimeSpan.FromSeconds(retryAfter.Value));
                return ValueTask.FromResult<TimeSpan?>(null);
            },
        };

        RetryAfter = new ResiliencePipelineBuilder().AddRetry(options).Build();

        MessageEdit = new ResiliencePipelineBuilder<Message>().AddFallback(fbOption).AddPipeline(RetryAfter).Build();
    }
}
