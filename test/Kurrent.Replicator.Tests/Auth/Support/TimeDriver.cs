using Microsoft.Extensions.Time.Testing;

namespace Kurrent.Replicator.Tests.Auth.Support;

public static class TimeDriver {
    /// <summary>Advances fake time in steps until the task completes (or the step budget runs out).</summary>
    public static async Task<T> Drive<T>(Task<T> task, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        await Drive((Task)task, time, step, maxSteps);

        return await task;
    }

    public static async Task Drive(Task task, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        for (var i = 0; i < maxSteps && !task.IsCompleted; i++) {
            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Advances fake time until the condition holds.</summary>
    public static async Task Until(Func<bool> condition, FakeTimeProvider time, TimeSpan? step = null, int maxSteps = 5000) {
        for (var i = 0; i < maxSteps && !condition(); i++) {
            time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(2);
        }

        if (!condition()) throw new TimeoutException("Condition not reached");
    }
}
