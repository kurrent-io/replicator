using Kurrent.Replicator.Shared.Logging;
using GreenPipes;

namespace Kurrent.Replicator;

public class LoggingFilter<T> : IFilter<T> where T : class, PipeContext {
    // ReSharper disable once StaticMemberInGenericType
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public async Task Send(T context, IPipe<T> next) {
        try {
            await next.Send(context).ConfigureAwait(false);
        }
        catch (Exception e) {
            if (context is IEventDetailsContext eventDetailsContext) {
                Log.Error(
                    e,
                    "Error occured in the {Type} pipe {@Event}: {Message}",
                    typeof(T).Name,
                    eventDetailsContext.EventDetails,
                    e.Message
                );
            }
            else {
                Log.Error(e, "Error occured in the {Type} pipe: {Message}", typeof(T).Name, e.Message);
            }

            throw;
        }
    }

    public void Probe(ProbeContext context) { }
}

public class LoggingFilterSpecification<T> : IPipeSpecification<T> where T : class, PipeContext {
    public void Apply(IPipeBuilder<T> builder) => builder.AddFilter(new LoggingFilter<T>());

    public IEnumerable<ValidationResult> Validate() {
        yield return this.Success("Logging is good");
    }
}

public static class LoggingFilterExtensions {
    public static void UseLog<T>(this IPipeConfigurator<T> cfg) where T : class, PipeContext
        => cfg.AddPipeSpecification(new LoggingFilterSpecification<T>());
}