namespace Kurrent.Replicator.Shared.Contracts;

public record StreamMetadata(
    int?       MaxCount,
    TimeSpan?  MaxAge,
    long?      TruncateBefore,
    TimeSpan?  CacheControl,
    StreamAcl? StreamAcl
);