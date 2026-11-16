using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MoreUseResources.Core;

public readonly record struct JitProgress(int Prepared, int Failed, int Skipped, double WorkMs, bool Complete);

/// <summary>Consumes managed metadata on the controller's low-priority workers.</summary>
public sealed class JitPrewarm
{
    private const int MaxPreparedMethods = 8000;
    private const int MaxEligiblePerAssembly = 1024;
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private readonly object gate = new();
    private readonly HashSet<MethodBase> patched;
    private readonly IEnumerator<MethodInfo?> candidates;
    private readonly Queue<MethodInfo> deferred = new();
    private int deferredCount;
    private int reserved;
    private int prepared;
    private int failed;
    private int skipped;
    private volatile bool sourceEnded;
    private long ticks;

    public JitPrewarm(IEnumerable<Assembly> assemblies, IEnumerable<MethodBase> patchedMethods)
    {
        patched = new HashSet<MethodBase>(patchedMethods);
        candidates = Enumerate(assemblies).GetEnumerator();
    }

    public JitProgress Progress
    {
        get
        {
            int done = Volatile.Read(ref prepared);
            int errors = Volatile.Read(ref failed);
            bool complete = done + errors >= MaxPreparedMethods
                || (sourceEnded && Volatile.Read(ref deferredCount) == 0 && done + errors == Volatile.Read(ref reserved));
            return new JitProgress(done, errors, Volatile.Read(ref skipped),
                Interlocked.Read(ref ticks) * 1000d / Stopwatch.Frequency, complete);
        }
    }

    public static bool IsEligible(MethodBase method)
    {
        if (method is not MethodInfo || method.DeclaringType?.ContainsGenericParameters != false
            || method.IsGenericMethod || method.IsAbstract || method.IsSpecialName || method.Name.StartsWith('<')
            || (method.Attributes & MethodAttributes.PinvokeImpl) != 0
            || (method.CallingConvention & CallingConventions.VarArgs) != 0)
            return false;

        const MethodImplAttributes excluded = MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask
            | MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized;
        return (method.GetMethodImplementationFlags() & excluded) == MethodImplAttributes.IL;
    }

    public void RunSlice(Func<bool> allowed, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 16 && Stopwatch.GetElapsedTime(start).TotalMilliseconds < 8; i++)
        {
            if (token.IsCancellationRequested || !allowed()) break;
            MethodInfo? method;
            lock (gate)
            {
                if (reserved == MaxPreparedMethods) break;
                if (deferred.TryDequeue(out method))
                {
                    Volatile.Write(ref deferredCount, deferred.Count);
                }
                else
                {
                    if (sourceEnded) break;
                    if (!candidates.MoveNext())
                    {
                        sourceEnded = true;
                        candidates.Dispose();
                        break;
                    }
                    method = candidates.Current;
                    if (method == null) continue;
                }
                Interlocked.Increment(ref reserved);
            }
            if (token.IsCancellationRequested || !allowed() || Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 8)
            {
                lock (gate)
                {
                    deferred.Enqueue(method);
                    Volatile.Write(ref deferredCount, deferred.Count);
                    Interlocked.Decrement(ref reserved);
                }
                break;
            }

            bool success = false;
            long methodStart = Stopwatch.GetTimestamp();
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                success = true;
            }
            catch (Exception ex) when (ex is MemberAccessException or InvalidOperationException or TypeLoadException
                or NotSupportedException or BadImageFormatException or FileNotFoundException
                or FileLoadException or InvalidProgramException or ArgumentException)
            {
            }
            finally
            {
                Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - methodStart);
                if (success) Interlocked.Increment(ref prepared);
                else Interlocked.Increment(ref failed);
            }
        }
    }

    private IEnumerable<MethodInfo?> Enumerate(IEnumerable<Assembly> assemblies)
    {
        foreach (Assembly assembly in assemblies)
        {
            if (assembly.IsDynamic) continue;
            Type?[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch (Exception ex) when (ex is TypeLoadException or FileLoadException or FileNotFoundException) { continue; }

            int eligible = 0;
            foreach (Type? type in types)
            {
                if (type == null || type.ContainsGenericParameters || type.IsInterface) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(Flags); }
                catch (Exception ex) when (ex is TypeLoadException or FileLoadException or FileNotFoundException) { continue; }
                foreach (MethodInfo method in methods)
                {
                    if (!IsEligible(method) || patched.Contains(method))
                    {
                        Interlocked.Increment(ref skipped);
                        yield return null;
                        continue;
                    }
                    yield return method;
                    if (++eligible >= MaxEligiblePerAssembly) break;
                }
                if (eligible >= MaxEligiblePerAssembly) break;
            }
        }
    }
}
