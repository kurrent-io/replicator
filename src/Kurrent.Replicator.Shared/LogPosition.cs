namespace Kurrent.Replicator.Shared; 

public record LogPosition(long EventNumber, ulong EventPosition) {
    public static readonly LogPosition Start = new(0, 0);
}