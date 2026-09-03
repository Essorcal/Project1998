using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shared;

/// <summary>
/// Upgrades a raw character JSON object one schema at a time, before it is bound to
/// <see cref="Character"/>. Field renames belong here: move the old member to the new name in the
/// appropriate step, then advance the version. Typed deserialization deliberately rejects any member a
/// step failed to consume, so deleting or renaming a persisted field cannot silently reset player state.
/// Upgrades are forward-only: a blob stamped newer than this build is refused, so rolling back after a
/// schema bump also requires rolling the character data back to a compatible version.
/// </summary>
public static class CharacterUpgrader
{
    private static readonly Action<JsonObject>[] Steps =
    {
        UpgradeSchemaZeroToOne,
    };

    /// <summary>Upgrade <paramref name="root"/> in place to <see cref="Character.CurrentSchemaVersion"/>.</summary>
    public static JsonObject Upgrade(JsonNode? root)
    {
        if (root is not JsonObject blob)
            throw new JsonException("Character JSON root must be an object.");

        int version = ReadVersion(blob);
        if (version < 0)
            throw new JsonException($"Character SchemaVersion cannot be negative ({version}).");
        if (version > Character.CurrentSchemaVersion)
            throw new JsonException(
                $"Character schema {version} is newer than supported schema {Character.CurrentSchemaVersion}.");
        if (Steps.Length != Character.CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Character schema is {Character.CurrentSchemaVersion}, but {Steps.Length} upgrade steps exist.");

        while (version < Character.CurrentSchemaVersion)
        {
            Steps[version](blob);
            int next = ReadVersion(blob);
            if (next != version + 1)
                throw new JsonException(
                    $"Character upgrade step {version} produced schema {next}, expected {version + 1}.");
            version = next;
        }

        return blob;
    }

    private static int ReadVersion(JsonObject blob)
    {
        if (!blob.TryGetPropertyValue(nameof(Character.SchemaVersion), out var node) || node is null)
            return 0;
        if (node is JsonValue value && value.TryGetValue<int>(out int version))
            return version;
        throw new JsonException("Character SchemaVersion must be an integer.");
    }

    private static void UpgradeSchemaZeroToOne(JsonObject blob)
    {
        // Schema 0 is every blob written before explicit versioning. Its member names already match schema
        // 1, so the only upgrade is making that fact explicit.
        blob[nameof(Character.SchemaVersion)] = 1;
    }
}
