using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ModReadService
    {
        public static BridgeResponse ListMods(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string scope = context.Request.Get("scope") ?? "loaded";
            if (!string.Equals(scope, "loaded", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scope, "installed", StringComparison.OrdinalIgnoreCase))
            {
                return BridgeResponse.Error(400, "Scope must be loaded or installed.");
            }
            scope = scope.ToLowerInvariant();
            string query = context.Request.Get("query") ?? "";
            ModSnapshot snapshot = new ModSnapshot();
            IEnumerable<ModEntry> source = scope == "loaded"
                ? snapshot.Loaded.Select(content => snapshot.Entry(content.PackageId))
                : snapshot.Installed.Select(metadata => snapshot.Entry(metadata.PackageId))
                    .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase);
            source = source.Where(mod => Matches(mod.Name, query) || Matches(mod.PackageId, query));
            Page<ModEntry> page = new Page<ModEntry>(source, context.Request);
            Dictionary<string, object> data = SessionInfo(snapshot);
            data["scope"] = scope;
            data["query"] = query;
            data["mods"] = context.Request.IdsOnly
                ? (object)page.Items.Select(mod => mod.PackageId).ToArray()
                : page.Items.Select(mod => SerializeMod(mod, snapshot)).ToArray();
            return ReadEnvelope.Ok(context, data, page.Truncated, page.NextCursor);
        }

        public static BridgeResponse GetMod(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            ModSnapshot snapshot = new ModSnapshot();
            ModEntry mod = snapshot.Entry(route["packageId"]);
            if (mod.Metadata == null && mod.Content == null)
            {
                return BridgeResponse.Error(404, "Mod not found.");
            }
            Dictionary<string, object> data = SessionInfo(snapshot);
            Dictionary<string, object> details = SerializeMod(mod, snapshot);
            ModMetaData metadata = mod.Metadata;
            if (metadata != null)
            {
                details["authors"] = metadata.Authors.ToArray();
                details["supportedVersions"] = (metadata.SupportedVersionsReadOnly ?? new List<Version>())
                    .Select(version => version.ToString()).ToArray();
                details["dependencies"] = (metadata.Dependencies ?? new List<ModDependency>())
                    .Where(dependency => dependency != null).Select(dependency => Dto.Obj(
                        Dto.Field("packageId", dependency.packageId),
                        Dto.Field("name", dependency.displayName),
                        Dto.Field("alternativePackageIds", dependency.alternativePackageIds ?? new List<string>())))
                    .ToArray();
                details["loadBefore"] = metadata.LoadBefore ?? new List<string>();
                details["loadAfter"] = metadata.LoadAfter ?? new List<string>();
                details["forceLoadBefore"] = metadata.ForceLoadBefore ?? new List<string>();
                details["forceLoadAfter"] = metadata.ForceLoadAfter ?? new List<string>();
                details["incompatibleWith"] = metadata.IncompatibleWith ?? new List<string>();
            }
            details["loadedAssemblies"] = mod.Content == null || mod.Content.assemblies == null
                ? new object[0]
                : mod.Content.assemblies.loadedAssemblies.Where(assembly => assembly != null)
                    .Select(assembly => assembly.GetName()).OrderBy(assembly => assembly.Name)
                    .Select(assembly => Dto.Obj(
                        Dto.Field("name", assembly.Name),
                        Dto.Field("version", assembly.Version == null ? null : assembly.Version.ToString())))
                    .ToArray();
            AddSettingsCategories(details, mod.Content);
            data["mod"] = details;
            return ReadEnvelope.Ok(context, data);
        }

        private static Dictionary<string, object> SerializeMod(ModEntry mod, ModSnapshot snapshot)
        {
            int loadOrder = Array.FindIndex(snapshot.Loaded, content => SameId(content.PackageId, mod.PackageId));
            int configuredOrder = Array.FindIndex(snapshot.Configured, id => SameId(id, mod.PackageId));
            ModMetaData metadata = mod.Metadata;
            return Dto.Obj(
                Dto.Field("packageId", mod.PackageId),
                Dto.Field("name", mod.Name),
                Dto.Field("modVersion", metadata == null || string.IsNullOrWhiteSpace(metadata.ModVersion) ? null : metadata.ModVersion),
                Dto.Field("source", metadata == null ? "unknown" : SourceName(metadata.Source)),
                Dto.Field("loaded", mod.Content != null),
                Dto.Field("enabled", configuredOrder >= 0),
                Dto.Field("loadOrder", loadOrder < 0 ? (int?)null : loadOrder),
                Dto.Field("configuredLoadOrder", configuredOrder < 0 ? (int?)null : configuredOrder),
                Dto.Field("versionCompatible", metadata == null || metadata.SupportedVersionsReadOnly == null
                    ? (bool?)null : metadata.VersionCompatible));
        }

        private static Dictionary<string, object> SessionInfo(ModSnapshot snapshot)
        {
            Dictionary<string, string> warnings = ModsConfig.GetModWarnings();
            return Dto.Obj(
                Dto.Field("gameVersion", VersionControl.CurrentVersionStringWithRev),
                Dto.Field("programState", Current.ProgramState.ToString()),
                Dto.Field("pendingConfigurationChanges", !snapshot.Loaded.Select(mod => mod.PackageId)
                    .SequenceEqual(snapshot.Configured, StringComparer.OrdinalIgnoreCase)),
                Dto.Field("warnings", Dto.Obj(
                    Dto.Field("scope", "configured"),
                    Dto.Field("items", warnings.Where(warning => !string.IsNullOrWhiteSpace(warning.Value))
                        .Select(warning => Dto.Obj(
                            Dto.Field("packageId", warning.Key),
                            Dto.Field("message", warning.Value))).ToArray()))));
        }

        private static void AddSettingsCategories(Dictionary<string, object> details, ModContentPack content)
        {
            List<string> categories = new List<string>();
            int failures = 0;
            if (content != null)
            {
                foreach (Mod handle in LoadedModManager.ModHandles.Where(handle => handle != null && handle.Content == content))
                {
                    try
                    {
                        string category = handle.SettingsCategory();
                        if (!string.IsNullOrWhiteSpace(category))
                        {
                            categories.Add(category);
                        }
                    }
                    catch (Exception)
                    {
                        failures++;
                    }
                }
            }
            details["settingsCategories"] = categories.Distinct().ToArray();
            details["settingsDiscoveryAvailable"] = content != null && failures == 0;
            if (failures > 0)
            {
                details["settingsCategoryErrors"] = failures;
            }
        }

        private static string SourceName(ContentSource source)
        {
            switch (source)
            {
                case ContentSource.OfficialModsFolder: return "official";
                case ContentSource.ModsFolder: return "local";
                case ContentSource.SteamWorkshop: return "workshop";
                default: return "unknown";
            }
        }

        private static bool Matches(string value, string query)
        {
            return string.IsNullOrWhiteSpace(query) || (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool SameId(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ModSnapshot
        {
            public readonly ModContentPack[] Loaded = LoadedModManager.RunningModsListForReading.Where(mod => mod != null).ToArray();
            public readonly ModMetaData[] Installed = ModLister.AllInstalledMods.Where(mod => mod != null).ToArray();
            public readonly string[] Configured = ModsConfig.ActiveModsInLoadOrder
                .Select(mod => mod == null ? null : mod.PackageId).ToArray();

            public ModEntry Entry(string packageId)
            {
                ModContentPack content = Loaded.FirstOrDefault(mod => SameId(mod.PackageId, packageId));
                return new ModEntry
                {
                    Metadata = Installed.FirstOrDefault(mod => SameId(mod.PackageId, packageId)) ?? content?.ModMetaData,
                    Content = content
                };
            }
        }

        private sealed class ModEntry
        {
            public ModMetaData Metadata;
            public ModContentPack Content;
            public string PackageId => Content == null ? Metadata.PackageId : Content.PackageId;
            public string Name => Content == null ? Metadata.Name : Content.Name;
        }
    }
}
