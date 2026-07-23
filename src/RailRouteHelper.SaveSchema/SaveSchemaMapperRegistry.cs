using RailRouteHelper.Core;
using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveSchema;

public sealed class SaveSchemaMapperRegistry
{
    private readonly IReadOnlyDictionary<GameVersion, ISaveSchemaMapper> mappers;

    public SaveSchemaMapperRegistry(IEnumerable<ISaveSchemaMapper> mappers)
    {
        ArgumentNullException.ThrowIfNull(mappers);

        var registrations = new Dictionary<GameVersion, ISaveSchemaMapper>();
        foreach (var mapper in mappers)
        {
            ArgumentNullException.ThrowIfNull(mapper);
            foreach (var version in mapper.SupportedGameVersions)
            {
                if (!registrations.TryAdd(version, mapper))
                {
                    throw new ArgumentException(
                        $"More than one mapper supports game version '{version}'.",
                        nameof(mappers));
                }
            }
        }

        this.mappers = registrations;
    }

    public IReadOnlyCollection<GameVersion> SupportedGameVersions =>
        mappers.Keys.Order().ToArray();

    public static SaveSchemaMapperRegistry CreateDefault() =>
        new([new SaveSchemaMapperV2_3()]);

    public SaveMappingResult Map(SaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = SaveTreeReader.RequireMap(document.Root, "$");
        var rawVersion = SaveTreeReader.RequireString(
            SaveTreeReader.Require(root, "gameVersion", "$.gameVersion"),
            "$.gameVersion");
        if (!GameVersion.TryParse(rawVersion, out var version)
            || !mappers.TryGetValue(version, out var mapper))
        {
            throw new UnsupportedGameVersionException(rawVersion);
        }

        return mapper.Map(document);
    }
}
