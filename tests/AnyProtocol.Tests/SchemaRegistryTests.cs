using AnyProtocol.Abstraction;
using Xunit;

namespace AnyProtocol.Tests;

public sealed class SchemaRegistryTests
{
    private const string VersionOne = """
        {
          "type": "object",
          "properties": {
            "id": { "type": "string" }
          },
          "required": ["id"]
        }
        """;

    [Fact]
    public async Task Registry_versions_compatible_schemas_and_deduplicates_identical_content()
    {
        var registry = new InMemorySchemaRegistry();
        var versionOne = await registry.RegisterAsync("orders.created", "json-schema", VersionOne);
        var duplicate = await registry.RegisterAsync("orders.created", "json-schema", VersionOne);
        var versionTwo = await registry.RegisterAsync(
            "orders.created",
            "json-schema",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "note": { "type": "string" }
              },
              "required": ["id"]
            }
            """);

        Assert.Equal(1, versionOne.Version);
        Assert.Same(versionOne, duplicate);
        Assert.Equal(2, versionTwo.Version);
        Assert.Equal(versionTwo, await registry.GetLatestAsync("orders.created"));
        Assert.Equal(versionOne, await registry.GetAsync("orders.created", 1));
    }

    [Fact]
    public async Task Backward_compatibility_rejects_a_new_required_property()
    {
        var registry = new InMemorySchemaRegistry();
        await registry.RegisterAsync("orders.created", "json-schema", VersionOne);

        var exception = await Assert.ThrowsAsync<SchemaCompatibilityException>(
            () => registry.RegisterAsync(
                    "orders.created",
                    "json-schema",
                    """
                    {
                      "type": "object",
                      "properties": {
                        "id": { "type": "string" },
                        "tenant": { "type": "string" }
                      },
                      "required": ["id", "tenant"]
                    }
                    """)
                .AsTask());

        Assert.Contains("tenant", exception.Message);
        Assert.Equal(1, (await registry.GetLatestAsync("orders.created"))!.Version);
    }

    [Fact]
    public async Task Full_compatibility_rejects_property_type_changes()
    {
        var registry = new InMemorySchemaRegistry();
        await registry.RegisterAsync("orders.created", "json-schema", VersionOne);

        var result = await registry.CheckCompatibilityAsync(
            "orders.created",
            "json-schema",
            """
            {
              "type": "object",
              "properties": {
                "id": { "type": "integer" }
              },
              "required": ["id"]
            }
            """,
            SchemaCompatibilityMode.Full);

        Assert.False(result.IsCompatible);
        Assert.Contains(result.Errors, static error => error.Contains("changes type"));
    }
}
