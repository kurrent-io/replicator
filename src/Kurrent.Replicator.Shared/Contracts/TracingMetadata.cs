using System.Diagnostics;

namespace Kurrent.Replicator.Shared.Contracts; 

public record TracingMetadata(ActivityTraceId TraceId, ActivitySpanId SpanId);