namespace AnyProtocol;

/// <summary>
/// Stores and resolves generated contract metadata registrations.
/// </summary>
public static class GeneratedContractMetadataRegistry
{
    /// <summary>
    /// Registers .
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <param name="serializableTypes">The serializable types.</param>
    public static void Register(Type contractType, IReadOnlyList<Type> serializableTypes)
        => GeneratedContractRegistry.RegisterMetadata(contractType, serializableTypes);

    /// <summary>
    /// Performs the is registered operation.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>true when the operation succeeds; otherwise, false.</returns>
    public static bool IsRegistered(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return GeneratedContractRegistry.IsMetadataRegistered(contractType);
    }

    /// <summary>
    /// Gets serializable types.
    /// </summary>
    /// <param name="contractType">The contract type.</param>
    /// <returns>The result of the get serializable types operation.</returns>
    public static IReadOnlyList<Type> GetSerializableTypes(Type contractType)
    {
        ArgumentNullException.ThrowIfNull(contractType);
        return GeneratedContractRegistry.GetSerializableTypes(contractType);
    }
}
