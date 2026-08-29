// Define the IgnoresAccessChecksToAttribute class required to use publicized assemblies at runtime.
// Define the class only if the project is built by Plugin Loader, because the Krafs.Publicizer
// provides this already if the project is built directly in an IDE or by running msbuild.
#if !LOCAL_BUILD

namespace System.Runtime.CompilerServices;

// Required when Pulsar builds the plugin; Krafs.Publicizer supplies this for local builds.
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class IgnoresAccessChecksToAttribute : Attribute
{
    public IgnoresAccessChecksToAttribute(string assemblyName)
    {
        AssemblyName = assemblyName;
    }

    public string AssemblyName { get; }
}

#endif
