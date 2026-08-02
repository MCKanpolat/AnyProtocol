using System.Reflection;

namespace AnyProtocol.Extensions;

internal static class ReflectionExtensions
{
    public static IEnumerable<PropertyInfo> GetAllProperties(this Type type)
    {
        var typeInfo = type.GetTypeInfo();

        return GetAllProperties(typeInfo);
    }

    public static IEnumerable<PropertyInfo> GetAllProperties(this TypeInfo typeInfo)
    {
        if (typeInfo.BaseType != null)
        {
            foreach (var prop in GetAllProperties(typeInfo.BaseType))
                yield return prop;
        }

        var specialGetPropertyNames = typeInfo.DeclaredMethods
            .Where(x => x.IsSpecialName && x.Name.StartsWith("get_") && !x.IsStatic)
            .Select(x => x.Name["get_".Length..]).Distinct();

        List<PropertyInfo> properties = typeInfo.DeclaredProperties
            .Where(x => specialGetPropertyNames.Contains(x.Name))
            .ToList();

        if (typeInfo.IsInterface)
        {
            IEnumerable<PropertyInfo> sourceProperties = properties
                .Concat(typeInfo.ImplementedInterfaces.SelectMany(x => x.GetTypeInfo().DeclaredProperties));

            foreach (var prop in sourceProperties)
                yield return prop;

            yield break;
        }

        foreach (var info in properties)
            yield return info;
    }
}