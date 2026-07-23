using RailRouteHelper.Core;
using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveSchema;

public interface ISaveSchemaMapper
{
    IReadOnlySet<GameVersion> SupportedGameVersions { get; }

    SaveMappingResult Map(SaveDocument document);
}

public sealed record SaveMappingResult(
    string SchemaId,
    OperationalSnapshot Snapshot,
    IReadOnlyList<SaveMappingDiagnostic> Diagnostics);

public sealed record SaveMappingDiagnostic(
    string Code,
    SaveMappingDiagnosticSeverity Severity,
    string Message);

public enum SaveMappingDiagnosticSeverity
{
    Information,
    Warning,
}
