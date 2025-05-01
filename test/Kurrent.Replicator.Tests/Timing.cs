using System.Diagnostics;
using Serilog;

namespace Kurrent.Replicator.Tests; 

public static class Timing {
    public static async Task Measure(string what, Task task) {
        var watch = new Stopwatch();
        watch.Start();
        await task;
        watch.Stop();
        Log.Information("{What} took {Time}", what, TimeSpan.FromMilliseconds(watch.ElapsedMilliseconds));
    }
}