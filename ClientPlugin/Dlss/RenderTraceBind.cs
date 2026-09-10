using System;
using System.Reflection;
using VRage.Utils;

namespace ClientPlugin.Dlss;

/// <summary>
/// Optional bind to Anomaly <c>ClientPlugin.ShaderFramework.RenderTrace</c>.
/// Begin/End are no-ops without Anomaly. Dump always writes SpaceEngineers.log.
/// </summary>
internal static class RenderTraceBind
{
    const string TypeName = "ClientPlugin.ShaderFramework.RenderTrace";
    static readonly object[] One = new object[1];
    static readonly object[] Two = new object[2];
    static readonly object Gate = new();
    static MethodInfo begin;
    static MethodInfo end;
    static MethodInfo dump;
    static MethodInfo dumpIfLost;
    static bool probed;

    public static void Begin(string name)
    {
        Ensure();
        InvokeName(begin, name);
    }

    public static void End(string name)
    {
        Ensure();
        InvokeName(end, name);
    }

    public static void Dump(string where, Exception e)
    {
        try
        {
            MyLog.Default.WriteLine("DLSS RenderTrace at " + where +
                                    (e != null ? ": " + e.GetType().Name + ": " + e.Message : ""));
        }
        catch
        {
            // ignored
        }

        Ensure();
        var method = dumpIfLost ?? dump;
        if (method == null)
            return;
        try
        {
            lock (Gate)
            {
                Two[0] = where;
                Two[1] = e;
                method.Invoke(null, Two);
            }
        }
        catch
        {
            // ignored
        }
    }

    static void InvokeName(MethodInfo method, string name)
    {
        if (name == null || method == null)
            return;
        try
        {
            One[0] = name;
            method.Invoke(null, One);
        }
        catch
        {
            // ignored
        }
    }

    static void Ensure()
    {
        if (probed)
            return;
        lock (Gate)
        {
            if (probed)
                return;
            probed = true;
            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly == null || assembly == typeof(RenderTraceBind).Assembly)
                        continue;
                    var type = assembly.GetType(TypeName, throwOnError: false, ignoreCase: false);
                    if (type == null)
                        continue;
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                    begin = type.GetMethod("Begin", flags, null, new[] { typeof(string) }, null);
                    end = type.GetMethod("End", flags, null, new[] { typeof(string) }, null);
                    dumpIfLost = type.GetMethod("DumpIfLost", flags, null, new[] { typeof(string), typeof(Exception) }, null);
                    dump = type.GetMethod("Dump", flags, null, new[] { typeof(string), typeof(Exception) }, null);
                    return;
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}
