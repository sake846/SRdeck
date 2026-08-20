using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SRdeckPlugin.Contracts;

namespace SRdeck.Services.Plugins;

internal readonly record struct PluginCodeWarmupResult(
    int AssemblyCount,
    int PreparedMethodCount,
    int SkippedMethodCount,
    int FailedMethodCount,
    TimeSpan Elapsed);

internal sealed class PluginCodeWarmupService
{
    public Task<PluginCodeWarmupResult> WarmUpAsync(
        IReadOnlyList<IPluginModule> modules,
        IReadOnlyList<Assembly>? additionalAssemblies = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modules);
        return Task.Run(() =>
        {
            Assembly[] assemblies = modules
                .Select(module => module.GetType().Assembly)
                .Concat(additionalAssemblies ?? [])
                .Where(assembly => !assembly.IsDynamic)
                .Distinct()
                .ToArray();
            return WarmUpAssemblies(assemblies, cancellationToken);
        }, cancellationToken);
    }

    internal static PluginCodeWarmupResult WarmUpAssemblies(
        IReadOnlyList<Assembly> assemblies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        long started = Stopwatch.GetTimestamp();
        int prepared = 0;
        int skipped = 0;
        int failed = 0;

        foreach (Assembly assembly in assemblies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Type type in GetLoadableTypes(assembly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type.ContainsGenericParameters)
                {
                    skipped++;
                    continue;
                }

                const BindingFlags flags = BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly;
                MethodBase[] methods;
                try
                {
                    methods = type
                        .GetMethods(flags)
                        .Cast<MethodBase>()
                        .Concat(type.GetConstructors(flags))
                        .ToArray();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine(
                        $"[PluginCodeWarmup] Could not inspect {type.FullName}: " +
                        exception.Message);
                    failed++;
                    continue;
                }

                foreach (MethodBase method in methods)
                {
                    if (TryPrepareMethod(method))
                        prepared++;
                    else if (CanPrepareMethod(method))
                        failed++;
                    else
                        skipped++;
                }
            }
        }

        return new PluginCodeWarmupResult(
            assemblies.Count,
            prepared,
            skipped,
            failed,
            Stopwatch.GetElapsedTime(started));
    }

    internal static bool TryPrepareMethod(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (!CanPrepareMethod(method)) return false;
        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"[PluginCodeWarmup] Skipped {method.DeclaringType?.FullName}.{method.Name}: " +
                exception.Message);
            return false;
        }
    }

    private static bool CanPrepareMethod(MethodBase method)
    {
        if (method.IsAbstract || method.ContainsGenericParameters ||
            method.DeclaringType?.ContainsGenericParameters != false)
        {
            return false;
        }

        MethodImplAttributes implementation = method.GetMethodImplementationFlags();
        return (method.Attributes & MethodAttributes.PinvokeImpl) == 0 &&
            (implementation & (MethodImplAttributes.Runtime |
                MethodImplAttributes.InternalCall)) == 0;
    }

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
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"[PluginCodeWarmup] Could not inspect {assembly.FullName}: " +
                exception.Message);
            return [];
        }
    }
}
