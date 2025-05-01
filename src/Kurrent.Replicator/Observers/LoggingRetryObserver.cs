using Kurrent.Replicator.Shared.Logging;
using GreenPipes;

namespace Kurrent.Replicator.Observers; 

public class LoggingRetryObserver : IRetryObserver {
    static readonly ILog Log = LogProvider.GetCurrentClassLogger();

    public Task PostCreate<T>(RetryPolicyContext<T> context) where T : class, PipeContext => Task.CompletedTask;

    public Task PostFault<T>(RetryContext<T> context) where T : class, PipeContext {
        Log.Error(context.Exception, "Error: {Error}, will retry", context.Exception.Message);
        return Task.CompletedTask;
    }

    public Task PreRetry<T>(RetryContext<T> context) where T : class, PipeContext => Task.CompletedTask;

    public Task RetryFault<T>(RetryContext<T> context) where T : class, PipeContext {
        Log.Error(context.Exception, "Error: {Error}, will fail", context.Exception.Message);
        return Task.CompletedTask;
    }

    public Task RetryComplete<T>(RetryContext<T> context) where T : class, PipeContext {
        Log.Info("Error recovered: {Error}", context.Exception.Message);
        return Task.CompletedTask;
    }
}