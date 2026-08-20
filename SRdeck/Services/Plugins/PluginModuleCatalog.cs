using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Concurrent;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

public static class PluginModuleCatalog
{
    private static readonly ConcurrentDictionary<string, byte> PluginDirectories =
        new(StringComparer.OrdinalIgnoreCase);
    private static int defaultResolverInstalled;

    public static IReadOnlyList<IPluginModule> Discover(string? baseDirectory = null)
    {
        string directory = Path.GetFullPath(baseDirectory ?? AppContext.BaseDirectory);
        RegisterPluginDirectory(directory);
        var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = assembly.GetName().Name;
            if (name?.StartsWith("SRdeckPlugin.", StringComparison.Ordinal) == true)
                assemblies[name] = assembly;
        }

        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.EnumerateFiles(directory, "SRdeckPlugin.*.dll"))
            {
                try
                {
                    string fullPath = Path.GetFullPath(path);
                    AssemblyName assemblyName = AssemblyName.GetAssemblyName(fullPath);
                    string? name = assemblyName.Name;
                    if (name is null || assemblies.ContainsKey(name)) continue;
                    assemblies[name] = AssemblyLoadContext.Default.LoadFromAssemblyPath(fullPath);
                }
                catch (BadImageFormatException) { }
                catch (FileLoadException) { }
            }
        }

        return InstantiateModules(assemblies.Values.SelectMany(GetLoadableTypes));
    }

    private static void RegisterPluginDirectory(string directory)
    {
        PluginDirectories.TryAdd(directory, 0);
        if (Interlocked.Exchange(ref defaultResolverInstalled, 1) != 0) return;
        AssemblyLoadContext.Default.Resolving += ResolvePluginDependency;
    }

    private static Assembly? ResolvePluginDependency(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrWhiteSpace(name.Name)) return null;
        foreach (string directory in PluginDirectories.Keys)
        {
            string candidate = Path.Combine(directory, $"{name.Name}.dll");
            if (!File.Exists(candidate)) continue;
            try { return context.LoadFromAssemblyPath(candidate); }
            catch (FileLoadException)
            {
                // A concurrent resolution may already have loaded this assembly.
                return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
            }
            catch (BadImageFormatException) { }
        }
        return null;
    }

    internal static IReadOnlyList<IPluginModule> InstantiateModules(IEnumerable<Type> types)
    {
        var modules = new List<IPluginModule>();
        foreach (Type type in types
                     .Where(IsPluginEntryPoint)
                     .OrderBy(candidate => candidate.FullName, StringComparer.Ordinal))
        {
            try
            {
                modules.Add((IPluginModule)Activator.CreateInstance(type)!);
            }
            catch (Exception exception)
            {
                Trace.WriteLine(
                    $"[Warning] [plugin.catalog.create.failed] Could not create plugin entry point '{type.FullName}': {exception}");
            }
        }
        return modules;
    }

    private static bool IsPluginEntryPoint(Type type) =>
        type is { IsAbstract: false, IsInterface: false, IsPublic: true } &&
        !type.ContainsGenericParameters &&
        typeof(IPluginModule).IsAssignableFrom(type) &&
        type.GetConstructor(Type.EmptyTypes) is not null;

    private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.OfType<Type>();
        }
    }
}
