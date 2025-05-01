// ReSharper disable SuggestBaseTypeForParameter

namespace Kurrent.Replicator.Shared.Contracts; 

public record StreamAcl(
    string[]? ReadRoles,
    string[]? WriteRoles,
    string[]? DeleteRoles,
    string[]? MetaReadRoles,
    string[]? MetaWriteRoles
);