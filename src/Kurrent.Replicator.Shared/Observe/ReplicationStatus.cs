using System.Diagnostics;

namespace Kurrent.Replicator.Shared.Observe; 

public static class ReplicationStatus {
    static Stopwatch Watch { get; } = new();

    public static void Start() {
        Watch.Restart();
        ReaderRunning = true;
    }

    public static void Stop() {
        Watch.Stop();
        ReaderRunning = false;
    }

    public static double ElapsedSeconds => Watch.Elapsed.TotalSeconds;
        
    public static bool ReaderRunning { get; set; }
}