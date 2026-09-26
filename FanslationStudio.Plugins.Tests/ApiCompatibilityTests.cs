using System.IO.Compression;
using System.Reflection;
using Mono.Cecil;

namespace FanslationStudio.Plugins.Tests
{
    /// <summary>
    /// Guards against calling .NET APIs that older Unity Mono runtimes don't implement.
    ///
    /// The Mono hosts are compiled against netstandard2.1, so the compiler happily binds to
    /// 2.1-only overloads (string.Split(char), Math.Clamp, StringBuilder.Append(StringBuilder)...).
    /// Unity 2020-era runtimes (e.g. LegendOfMortal) don't have them, and the result is a
    /// MissingMethodException at runtime - only discovered in-game.
    ///
    /// This inspects each woven plugin DLL plus every assembly Costura embeds in it, and resolves
    /// every base-library type/member reference against the netstandard2.0 reference assemblies.
    /// The IL2CPP host is excluded: it runs on .NET 6.
    /// </summary>
    public class ApiCompatibilityTests
    {
        private static readonly string BaselineDirectory =
            Path.Combine(AppContext.BaseDirectory, "Baselines", "netstandard2.0");

        [Theory]
        [InlineData("BepInEx5")]
        [InlineData("BepInEx6")]
        public void PluginOnlyUsesNetStandard20Apis(string host)
        {
            var pluginPath = GetPluginOutputPath(host);
            Assert.True(File.Exists(pluginPath), $"Plugin output not found - build the solution first: {pluginPath}");

            using var resolver = new DefaultAssemblyResolver();
            foreach (var directory in resolver.GetSearchDirectories())
                resolver.RemoveSearchDirectory(directory);
            resolver.AddSearchDirectory(BaselineDirectory);

            var readerParameters = new ReaderParameters { AssemblyResolver = resolver };
            var missing = new SortedSet<string>();

            using var plugin = ModuleDefinition.ReadModule(pluginPath, readerParameters);
            CollectMissing(plugin, missing);

            foreach (var (name, bytes) in ReadCosturaAssemblies(plugin))
            {
                using var embedded = ModuleDefinition.ReadModule(new MemoryStream(bytes), readerParameters);
                CollectMissing(embedded, missing, name);
            }

            Assert.True(missing.Count == 0,
                $"{host} references APIs missing from netstandard2.0 (will throw on older Unity Mono):{Environment.NewLine}"
                + string.Join(Environment.NewLine, missing));
        }

        private static string GetPluginOutputPath(string host)
        {
            return typeof(ApiCompatibilityTests).Assembly
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(a => a.Key == $"PluginOutput:{host}")
                .Value!;
        }

        private static IEnumerable<(string Name, byte[] Bytes)> ReadCosturaAssemblies(ModuleDefinition plugin)
        {
            foreach (var resource in plugin.Resources.OfType<EmbeddedResource>())
            {
                var name = resource.Name;
                if (!name.StartsWith("costura.") || !(name.EndsWith(".dll") || name.EndsWith(".dll.compressed")))
                    continue;

                using var stream = resource.GetResourceStream();
                using var buffer = new MemoryStream();
                if (name.EndsWith(".compressed"))
                {
                    using var deflate = new DeflateStream(stream, CompressionMode.Decompress);
                    deflate.CopyTo(buffer);
                }
                else
                {
                    stream.CopyTo(buffer);
                }

                yield return (name, buffer.ToArray());
            }
        }

        private static void CollectMissing(ModuleDefinition module, ISet<string> missing, string? label = null)
        {
            label ??= module.Name;

            foreach (var type in module.GetTypeReferences())
            {
                if (IsBaseLibrary(type) && !TryResolve(type.Resolve))
                    missing.Add($"{label}: type {type.FullName}");
            }

            foreach (var member in module.GetMemberReferences())
            {
                // Multi-dimensional array accessors (Get/Set/Address) are runtime-provided, not real members.
                if (member.DeclaringType is ArrayType)
                    continue;

                var declaringType = member.DeclaringType;
                while (declaringType is TypeSpecification specification)
                    declaringType = specification.ElementType;

                if (IsBaseLibrary(declaringType) && !TryResolve(member.Resolve))
                    missing.Add($"{label}: {member.FullName}");
            }
        }

        private static bool IsBaseLibrary(TypeReference type)
        {
            var scope = type.Scope?.Name ?? string.Empty;
            return scope == "netstandard"
                || scope == "mscorlib"
                || scope.StartsWith("System")
                || scope.StartsWith("Microsoft.Win32");
        }

        private static bool TryResolve(Func<object?> resolve)
        {
            try
            {
                return resolve() != null;
            }
            catch (AssemblyResolutionException)
            {
                return false;
            }
        }
    }
}
