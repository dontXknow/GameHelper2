// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RuntimeCheck
{
    using System.ComponentModel;
    using System.Diagnostics;
    using System.Reflection;
    using System.Reflection.PortableExecutable;
    using System.Runtime.InteropServices;
    using System.Runtime.CompilerServices;
    using System.Runtime.Loader;
    using System.Security.Cryptography;
    using System.Text;
    using GameOffsets;
    using GameOffsets.Natives;
    using GameOffsets.Objects;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States;
    using GameOffsets.Objects.States.InGameState;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>Verifies staged dependencies without starting the overlay or sending input.</summary>
    internal static partial class Program
    {
        private static string root = "";

        private static int Main(string[] args)
        {
            try
            {
                var expectedArguments = args.FirstOrDefault() switch
                {
                    "smoke" or "reader-tests" => 3,
                    "probe" or "entities" or "layouts" or "base-live" => 4,
                    _ => 0,
                };
                if (expectedArguments == 0 || args.Length != expectedArguments)
                {
                    throw new ArgumentException(
                        "Usage: RuntimeCheck <smoke|reader-tests> <runtime-dir> <output-dir>\n" +
                        "       RuntimeCheck <probe|entities|base-live> <runtime-dir> <pid> <output-dir>\n" +
                        "       RuntimeCheck layouts <baseline-runtime-dir> <updated-offsets-dll> <output-dir>");
                }

                root = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetFullPath(args[^1]));
                AssemblyLoadContext.Default.Resolving += Resolve;
                if (args[0] == "smoke") { Smoke(args[2]); }
                else if (args[0] == "probe") { Probe(int.Parse(args[2]), args[3]); }
                else if (args[0] == "entities") { Probe(int.Parse(args[2]), args[3], true); }
                else if (args[0] == "layouts") { CompareLayouts(args[2], args[3]); }
                else if (args[0] == "reader-tests") { ReaderTests(args[2]); }
                else if (args[0] == "base-live") { BaseLive(int.Parse(args[2]), args[3]); }
                return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }

        private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var file = Path.Combine(root, name.Name + ".dll");
            if (File.Exists(file)) { return context.LoadFromAssemblyPath(file); }
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "Plugins")))
            {
                file = Path.Combine(dir, name.Name + ".dll");
                if (File.Exists(file)) { return context.LoadFromAssemblyPath(file); }
            }
            return null;
        }

        private static void CompareLayouts(string updated, string evidence)
        {
            var before = OffsetLayouts(Path.Combine(root, "GameOffsets.dll"));
            var after = OffsetLayouts(updated);
            var differences = before.Keys.Union(after.Keys).Where(k => before.GetValueOrDefault(k) != after.GetValueOrDefault(k))
                .Select(k => new { Key = k, Before = before.GetValueOrDefault(k), After = after.GetValueOrDefault(k) }).ToArray();
            File.WriteAllText(Path.Combine(evidence, "offset-layout-comparison.json"), JsonConvert.SerializeObject(new { BeforeEntries = before.Count, AfterEntries = after.Count, Differences = differences }, Formatting.Indented));
            if (differences.Length != 0) { throw new InvalidDataException(JsonConvert.SerializeObject(differences, Formatting.Indented)); }
            Console.WriteLine($"Offsets layout preserved: {before.Count} type/field/constant entries identical.");
        }

        private static Dictionary<string, string> OffsetLayouts(string path)
        {
            var context = new AssemblyLoadContext(Guid.NewGuid().ToString(), true);
            try
            {
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(path));
                var result = new Dictionary<string, string>();
                foreach (var t in assembly.GetTypes().Where(t => !t.ContainsGenericParameters))
                {
                    if (t.IsValueType && !t.IsEnum)
                    {
                        try { result[t.FullName + ".Size"] = Marshal.SizeOf(t).ToString(); }
                        catch (ArgumentException) { result[t.FullName + ".Size"] = "NotMarshalable"; }
                    }
                    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        var value = f.FieldType.FullName + ";" + f.Attributes;
                        if (f.IsLiteral) { value += ";" + f.GetRawConstantValue(); }
                        if (!f.IsStatic && t.IsValueType && !t.IsEnum)
                        {
                            try { value += ";offset=" + Marshal.OffsetOf(t, f.Name); }
                            catch (ArgumentException) { value += ";offset=NotMarshalable"; }
                        }
                        result[t.FullName + "." + f.Name] = value;
                    }
                }
                return result;
            }
            finally { context.Unload(); }
        }

        private static void Smoke(string evidence)
        {
            var results = new List<string>();
            var core = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "GameHelper.dll"));
            var types = core.GetTypes();
            results.Add($"Core loaded: {types.Length} types");
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(root, "Plugins")))
            {
                var dll = Directory.EnumerateFiles(dir, "*.dll").FirstOrDefault(x =>
                    Path.GetFileNameWithoutExtension(x).Equals(Path.GetFileName(dir), StringComparison.OrdinalIgnoreCase));
                if (dll == null) { continue; }
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                var pluginTypes = assembly.GetTypes();
                var count = pluginTypes.Count(t => t.IsSealed && t.GetInterfaces().Any(i => i.FullName == "GameHelper.Plugin.IPCore"));
                if (count != 1) { throw new InvalidDataException($"{dll}: expected one plugin, found {count}"); }
                results.Add($"Plugin load passed: {Path.GetFileName(dir)}");
            }

            var ahk = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "AutoHotKeyTrigger");
            var settingsType = ahk.GetType("AutoHotKeyTrigger.AutoHotKeyTriggerSettings", true)!;
            var settingsPath = Path.Combine(root, "Plugins", "AutoHotKeyTrigger", "config", "settings.txt");
            var hasStoredSettings = File.Exists(settingsPath);
            var settingsText = hasStoredSettings
                ? File.ReadAllText(settingsPath)
                : JsonConvert.SerializeObject(Activator.CreateInstance(settingsType));
            var settings = JsonConvert.DeserializeObject(settingsText, settingsType)
                ?? throw new InvalidDataException("Settings deserialize returned null.");
            var json = JObject.Parse(settingsText);
            var conditionType = ahk.GetType("AutoHotKeyTrigger.ProfileManager.DynamicConditions.DynamicCondition", true)!;
            var function = conditionType.GetField("func", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var error = conditionType.GetField("lastException", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int checkedExpressions = 0;
            foreach (var expression in json.SelectTokens("$..conditionSource").Select(t => t.Value<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
            {
                var condition = Activator.CreateInstance(conditionType, expression);
                if (function.GetValue(condition) == null) { throw new InvalidDataException($"Expression failed: {expression}: {error.GetValue(condition)}"); }
                checkedExpressions++;
            }
            results.Add($"{(hasStoredSettings ? "Stored" : "Default")} settings deserialize and {checkedExpressions} distinct expressions compile; expressions were not executed");
            var roundTrip = JObject.Parse(JsonConvert.SerializeObject(settings));
            foreach (var key in new[] { "PrioritizeManualInput", "ManualInputGraceSeconds", "CurrentProfile" })
            {
                if (!JToken.DeepEquals(json[key], roundTrip[key])) { throw new InvalidDataException($"Setting changed: {key}"); }
            }
            results.Add("Input priority and active profile preserved through JSON round trip");

            var launcher = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Launcher.dll"));
            var transform = launcher.GetType("Launcher.GameHelperTransformer", true)!.GetMethod("TransformExecutable", BindingFlags.Static | BindingFlags.NonPublic)!;
            var output = Path.Combine(evidence, "Launcher-transform-test.exe");
            var sourceExecutable = Path.Combine(root, "GameHelper.exe");
            var originalVersion = FileVersionInfo.GetVersionInfo(sourceExecutable).FileVersion;
            transform.Invoke(null, new object[] { sourceExecutable, output, "PackageUpdateSmoke" });
            var info = FileVersionInfo.GetVersionInfo(output);
            if (info.ProductName != "PackageUpdateSmoke" || info.FileVersion != originalVersion) { throw new InvalidDataException("Launcher resource transformation failed."); }
            using (var stream = File.OpenRead(output))
            using (var pe = new PEReader(stream))
            {
                if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) { throw new InvalidDataException("Launcher output is not x64."); }
            }
            results.Add("AsmResolver 6 launcher transformation preserves x64 PE and file version; transformed executable was not started");
            results.Add($"Newtonsoft.Json: {typeof(JsonConvert).Assembly.GetName().Version}");
            results.Add($"Dynamic LINQ: {AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "System.Linq.Dynamic.Core").GetName().Version}");
            File.WriteAllLines(Path.Combine(evidence, "smoke-results.txt"), results);
            foreach (var result in results) { Console.WriteLine(result); }
        }

        private static void Probe(int processId, string evidence, bool inspectEntities = false)
        {
            using var process = Process.GetProcessById(processId);
            var module = process.MainModule ?? throw new InvalidOperationException("Game module unavailable.");
            var imagePath = module.FileName;
            var imageBase = module.BaseAddress.ToInt64();
            var handle = OpenProcess(0x0010, false, processId);
            if (handle == IntPtr.Zero) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try
            {
                using var stream = File.OpenRead(imagePath);
                using var pe = new PEReader(stream);
                var matches = new List<object>();
                long gameStatesTarget = 0;
                foreach (var pattern in StaticOffsetsPatterns.Patterns)
                {
                    var hits = new List<object>();
                    foreach (var section in pe.PEHeaders.SectionHeaders.Where(s => (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0))
                    {
                        var data = pe.GetSectionData(section.VirtualAddress).GetContent().ToArray();
                        var checks = Enumerable.Range(0, pattern.Data.Length).Where(i => pattern.Mask[i]).ToArray();
                        for (var i = 0; i <= data.Length - pattern.Data.Length; i++)
                        {
                            var isMatch = true;
                            foreach (var j in checks)
                            {
                                if (data[i + j] != pattern.Data[j]) { isMatch = false; break; }
                            }
                            if (!isMatch) { continue; }
                            var rva = section.VirtualAddress + i;
                            var live = Read(handle, imageBase + rva, pattern.Data.Length);
                            var valid = checks.All(j => live[j] == pattern.Data[j]);
                            var target = imageBase + rva + pattern.BytesToSkip + 4 + BitConverter.ToInt32(live, pattern.BytesToSkip);
                            if (pattern.Name == "Game States") { gameStatesTarget = target; }
                            string? value = null;
                            string? error = null;
                            try { value = Convert.ToHexString(Read(handle, target, 32)); }
                            catch (Win32Exception e) { error = e.Message; }
                            hits.Add(new { MatchRva = $"0x{rva:X}", LiveMatches = valid, Target = $"0x{target:X}", TargetValue = value, ReadError = error, Context = Convert.ToHexString(data.AsSpan(Math.Max(0, i - 16), Math.Min(80, data.Length - Math.Max(0, i - 16)))) });
                        }
                    }
                    matches.Add(new { pattern.Name, Count = hits.Count, Hits = hits });
                }
                var structures = new Dictionary<string, object>();
                try { ProbeStructures(handle, gameStatesTarget, structures, inspectEntities); }
                catch (Exception e) { structures["ReadError"] = e.Message; }
                var report = new { CapturedAtUtc = DateTimeOffset.UtcNow, ProcessId = processId, Path = imagePath, ImageSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))), OffsetsSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(StaticOffsetsPatterns).Assembly.Location))), ImageBase = $"0x{imageBase:X}", ReadOnly = true, Patterns = matches, Structures = structures };
                var json = JsonConvert.SerializeObject(report, Formatting.Indented);
                File.WriteAllText(Path.Combine(evidence, inspectEntities ? "entity-probe.json" : "signature-probe.json"), json);
                Console.WriteLine(json);
            }
            finally { CloseHandle(handle); }
        }

        private static void ProbeStructures(IntPtr handle, long target, Dictionary<string, object> result, bool inspectEntities)
        {
            var states = ReadStruct<GameStateStaticOffset>(handle, target);
            var stateData = ReadStruct<GameStateOffset>(handle, states.GameState.ToInt64());
            var inGameAddress = stateData.States[4].X.ToInt64();
            result["GameStates"] = $"0x{states.GameState:X}";
            result["InGameState"] = $"0x{inGameAddress:X}";
            var inGame = ReadStruct<InGameStateOffset>(handle, inGameAddress);
            var mouse = new Dictionary<string, object> { ["Host"] = $"0x{inGame.MouseOverHostPtr:X}" };
            result["MouseOver"] = mouse;
            try
            {
                var host = ReadStruct<MouseOverHostStruct>(handle, inGame.MouseOverHostPtr.ToInt64());
                mouse["Sub"] = $"0x{host.MouseOverSubPtr:X}";
                var sub = ReadStruct<MouseOverSubStruct>(handle, host.MouseOverSubPtr.ToInt64());
                mouse["Candidate"] = $"0x{sub.EntityPtr:X}";
                if (sub.EntityPtr != IntPtr.Zero)
                {
                    var candidate = ReadStruct<EntityOffsets>(handle, sub.EntityPtr.ToInt64());
                    mouse["VTable"] = $"0x{candidate.ItemBase.VTablePtr:X}";
                    mouse["Details"] = $"0x{candidate.ItemBase.EntityDetailsPtr:X}";
                    mouse["Id"] = candidate.Id;
                    mouse["ValidFlag"] = candidate.IsValid;
                }
            }
            catch (Exception e) { mouse["ReadError"] = e.Message; }
            result["AreaInstance"] = $"0x{inGame.AreaInstanceData:X}";
            var area = ReadStruct<AreaInstanceOffsets>(handle, inGame.AreaInstanceData.ToInt64());
            if (inspectEntities)
            {
                result["AwakeMap"] = ProbeMap(handle, area.Entities.AwakeEntities);
                result["SleepingMap"] = ProbeMap(handle, area.Entities.SleepingEntities);
                result["TerrainPointerSummary"] = ProbeTerrain(handle, area.TerrainMetadata);
            }
            result["AreaLevel"] = area.CurrentAreaLevel;
            result["AreaHash"] = $"0x{area.CurrentAreaHash:X}";
            result["TerrainTiles"] = area.TerrainMetadata.TotalTiles.ToString();
            result["Player"] = $"0x{area.PlayerInfo.LocalPlayerPtr:X}";
            var entity = ReadStruct<EntityOffsets>(handle, area.PlayerInfo.LocalPlayerPtr.ToInt64());
            result["PlayerId"] = entity.Id;
            result["PlayerValidByte"] = entity.IsValid;
            var details = ReadStruct<EntityDetails>(handle, entity.ItemBase.EntityDetailsPtr.ToInt64());
            result["PlayerPath"] = ReadWString(handle, entity.ItemBase.EntityDetailsPtr.ToInt64() + (int)Marshal.OffsetOf<EntityDetails>(nameof(EntityDetails.name)), details.name);
            var lookup = ReadStruct<ComponentLookUpStruct>(handle, details.ComponentLookUpPtr.ToInt64());
            var names = ReadVector<ComponentNameAndIndexStruct>(handle, lookup.ComponentsNameAndIndex.Data, 64);
            var pointers = ReadVector<IntPtr>(handle, entity.ItemBase.ComponentListPtr, 64);
            var components = new Dictionary<string, long>();
            foreach (var item in names)
            {
                if (item.NamePtr == IntPtr.Zero || item.Index < 0 || item.Index >= pointers.Length) { continue; }
                var name = ReadString(handle, item.NamePtr.ToInt64(), false);
                components[name] = pointers[item.Index].ToInt64();
            }
            result["Components"] = components.ToDictionary(p => p.Key, p => $"0x{p.Value:X}");
            if (components.TryGetValue("Life", out var lifeAddress))
            {
                var life = ReadStruct<LifeOffset>(handle, lifeAddress);
                result["Vitals"] = new { Health = new { life.Health.Current, life.Health.Total }, Mana = new { life.Mana.Current, life.Mana.Total }, EnergyShield = new { life.EnergyShield.Current, life.EnergyShield.Total } };
            }
            if (components.TryGetValue("Buffs", out var buffsAddress))
            {
                var buffs = ReadStruct<BuffsOffsets>(handle, buffsAddress);
                var addresses = ReadVector<IntPtr>(handle, buffs.StatusEffectPtr, 256);
                var values = new List<object>();
                foreach (var address in addresses.Where(a => a != IntPtr.Zero))
                {
                    try
                    {
                        var buff = ReadStruct<StatusEffectStruct>(handle, address.ToInt64());
                        var namePtr = ReadStruct<IntPtr>(handle, buff.BuffDefinationPtr.ToInt64());
                        values.Add(new { Name = ReadString(handle, namePtr.ToInt64(), true), buff.Charges, buff.RawStage, buff.TimeLeft, buff.TotalTime });
                    }
                    catch (Exception e) { values.Add(new { Address = $"0x{address:X}", Error = e.Message }); }
                }
                result["Buffs"] = values;
            }
            if (components.TryGetValue("Actor", out var actorAddress))
            {
                var actor = ReadStruct<ActorOffset>(handle, actorAddress);
                var skills = ReadVector<ActiveSkillStructure>(handle, actor.ActiveSkillsPtr, 256);
                var values = new List<object>();
                foreach (var skill in skills.Where(s => s.ActiveSkillPtr != IntPtr.Zero))
                {
                    try
                    {
                        var detail = ReadStruct<ActiveSkillDetails>(handle, skill.ActiveSkillPtr.ToInt64());
                        var namePtr = ReadStruct<IntPtr>(handle, detail.GrantedEffectsPerLevelDatRow.ToInt64());
                        values.Add(new { Name = ReadString(handle, namePtr.ToInt64(), true), detail.UseStage, detail.TotalUses, detail.TotalCooldownTimeInMs });
                    }
                    catch (Exception e) { values.Add(new { Address = $"0x{skill.ActiveSkillPtr:X}", Error = e.Message }); }
                }
                result["Skills"] = values;
                result["CooldownRows"] = ReadVector<ActiveSkillCooldown>(handle, actor.CooldownsPtr, 256).Select(c => new { c.ActiveSkillsDatId, c.MaxUses, c.TotalCooldownTimeInMs, Entries = c.TotalActiveCooldowns() }).ToArray();
            }
        }

        private static T ReadStruct<T>(IntPtr handle, long address) where T : unmanaged =>
            MemoryMarshal.Read<T>(Read(handle, address, Unsafe.SizeOf<T>()));

        private static object ProbeMap(IntPtr handle, StdMap map)
        {
            var samples = new List<object>();
            var items = new List<object>();
            var errors = new List<string>();
            if (map.Size < 0 || map.Size > 500000) { return new { Error = "Invalid map size", map.Size }; }
            if (map.Size == 0) { return new { map.Size }; }
            var head = ReadStruct<StdMapNode<EntityNodeKey, EntityNodeValue>>(handle, map.Head.ToInt64());
            var pending = new Queue<long>();
            var visited = new HashSet<long>();
            pending.Enqueue(head.Parent.ToInt64());
            int validNodes = 0, matchingIds = 0, invalidFlags = 0;
            while (pending.TryDequeue(out var address) && visited.Count < 5000)
            {
                if (address == 0 || address == map.Head.ToInt64() || !visited.Add(address)) { continue; }
                try
                {
                    var node = ReadStruct<StdMapNode<EntityNodeKey, EntityNodeValue>>(handle, address);
                    if (node.IsNil) { continue; }
                    if (node.Color > 1) { invalidFlags++; continue; }
                    validNodes++;
                    pending.Enqueue(node.Left.ToInt64()); pending.Enqueue(node.Right.ToInt64());
                    var entity = ReadStruct<EntityOffsets>(handle, node.Data.Value.EntityPtr.ToInt64());
                    if (entity.Id == node.Data.Key.id) { matchingIds++; }
                    var details = ReadStruct<EntityDetails>(handle, entity.ItemBase.EntityDetailsPtr.ToInt64());
                    var path = ReadWString(handle, entity.ItemBase.EntityDetailsPtr.ToInt64() + 8, details.name);
                    if (samples.Count < 15) { samples.Add(new { Key = node.Data.Key.id, EntityId = entity.Id, Entity = $"0x{node.Data.Value.EntityPtr:X}", Path = path }); }
                    if (items.Count < 40 && (path.Contains("Items", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/WorldItem", StringComparison.Ordinal)))
                    {
                        var lookup = ReadStruct<ComponentLookUpStruct>(handle, details.ComponentLookUpPtr.ToInt64());
                        var names = ReadVector<ComponentNameAndIndexStruct>(handle, lookup.ComponentsNameAndIndex.Data, 64);
                        var pointers = ReadVector<IntPtr>(handle, entity.ItemBase.ComponentListPtr, 64);
                        var components = new Dictionary<string, long>();
                        foreach (var entry in names)
                        {
                            if (entry.NamePtr == IntPtr.Zero || entry.Index < 0 || entry.Index >= pointers.Length) { continue; }
                            components[ReadString(handle, entry.NamePtr.ToInt64(), false)] = pointers[entry.Index].ToInt64();
                        }
                        if (components.TryGetValue("WorldItem", out var wrapper))
                        {
                            var itemAddress = ReadStruct<WorldItemOffsets>(handle, wrapper).ItemEntityPtr.ToInt64();
                            var item = ReadStruct<ItemStruct>(handle, itemAddress);
                            var itemDetails = ReadStruct<EntityDetails>(handle, item.EntityDetailsPtr.ToInt64());
                            var itemLookup = ReadStruct<ComponentLookUpStruct>(handle, itemDetails.ComponentLookUpPtr.ToInt64());
                            var itemNames = ReadVector<ComponentNameAndIndexStruct>(handle, itemLookup.ComponentsNameAndIndex.Data, 64);
                            var itemPointers = ReadVector<IntPtr>(handle, item.ComponentListPtr, 64);
                            foreach (var entry in itemNames.Where(e => e.Index >= 0 && e.Index < itemPointers.Length && e.NamePtr != IntPtr.Zero))
                            {
                                if (ReadString(handle, entry.NamePtr.ToInt64(), false) != "Base") { continue; }
                                items.Add(ProbeBase(handle, itemPointers[entry.Index].ToInt64(), path));
                            }
                        }
                        else if (components.TryGetValue("Base", out var baseAddress)) { items.Add(ProbeBase(handle, baseAddress, path)); }
                    }
                }
                catch (Exception e) { if (errors.Count < 20) { errors.Add($"0x{address:X}: {e.Message}"); } }
            }
            return new { map.Size, Head = $"0x{map.Head:X}", Visited = visited.Count, ValidNodes = validNodes, MatchingEntityIds = matchingIds, InvalidFlags = invalidFlags, Limited = pending.Count > 0, Errors = errors, Samples = samples, ItemSamples = items };
        }

        private static object ProbeBase(IntPtr handle, long address, string path)
        {
            var data = ReadStruct<BaseOffsets>(handle, address);
            var stringAddress = data.DisplayNameRowPtr.ToInt64() + BaseOffsets.DisplayNameOffset;
            var blob = Read(handle, stringAddress, 32);
            var possibleString = MemoryMarshal.Read<StdWString>(blob);
            string? decoded = null, error = null;
            try { decoded = ReadWString(handle, stringAddress, possibleString); }
            catch (Exception e) { error = e.Message; }
            return new { Path = path, Address = $"0x{address:X}", Row = $"0x{data.DisplayNameRowPtr:X}", RawStringBytes = Convert.ToHexString(blob), FirstWord = $"0x{possibleString.Buffer:X}", possibleString.Length, possibleString.Capacity, StdWStringValue = decoded, DecodeError = error };
        }

        private static object ProbeTerrain(IntPtr handle, TerrainStruct terrain)
        {
            var bytes = terrain.TileDetailsPtr.Last.ToInt64() - terrain.TileDetailsPtr.First.ToInt64();
            var expected = terrain.TotalTiles.X * terrain.TotalTiles.Y;
            if (expected <= 0 || expected > 200000 || bytes != expected * Unsafe.SizeOf<TileStructure>()) { return new { Error = "Terrain vector shape mismatch" }; }
            int nullTgt = 0, nullSubTile = 0, nonNullTgt = 0;
            var paths = new List<string>();
            for (var i = 0; i < expected; i += 1024)
            {
                var count = (int)Math.Min(1024, expected - i);
                var blob = Read(handle, terrain.TileDetailsPtr.First.ToInt64() + i * Unsafe.SizeOf<TileStructure>(), count * Unsafe.SizeOf<TileStructure>());
                foreach (var tile in MemoryMarshal.Cast<byte, TileStructure>(blob))
                {
                    if (tile.SubTileDetailsPtr == IntPtr.Zero) { nullSubTile++; }
                    if (tile.TgtFilePtr == IntPtr.Zero) { nullTgt++; }
                    else
                    {
                        nonNullTgt++;
                        if (paths.Count < 5)
                        {
                            var tgt = ReadStruct<TgtFileStruct>(handle, tile.TgtFilePtr.ToInt64());
                            paths.Add(ReadWString(handle, tile.TgtFilePtr.ToInt64() + 8, tgt.TgtPath));
                        }
                    }
                }
            }
            return new { Tiles = expected, NullTgt = nullTgt, NonNullTgt = nonNullTgt, NullSubTile = nullSubTile, Paths = paths };
        }

        private static T[] ReadVector<T>(IntPtr handle, StdVector vector, int maxCount) where T : unmanaged
        {
            var size = Unsafe.SizeOf<T>();
            var length = vector.Last.ToInt64() - vector.First.ToInt64();
            if (length == 0) { return Array.Empty<T>(); }
            if (length < 0 || length % size != 0 || length / size > maxCount || vector.End.ToInt64() < vector.Last.ToInt64()) { throw new InvalidDataException($"Invalid {typeof(T).Name} vector: {vector}"); }
            return MemoryMarshal.Cast<byte, T>(Read(handle, vector.First.ToInt64(), checked((int)length))).ToArray();
        }

        private static string ReadWString(IntPtr handle, long address, StdWString value)
        {
            if (value.Length < 0 || value.Length > 512) { throw new InvalidDataException("Invalid wide string length."); }
            if (value.Length == 0) { return ""; }
            return Encoding.Unicode.GetString(Read(handle, value.Capacity < 8 ? address : value.Buffer.ToInt64(), value.Length * 2));
        }

        private static string ReadString(IntPtr handle, long address, bool wide)
        {
            var bytes = new List<byte>();
            var step = wide ? 2 : 1;
            for (var i = 0; i < 512; i += step)
            {
                var next = Read(handle, address + i, step);
                if (next.All(b => b == 0)) { break; }
                bytes.AddRange(next);
            }
            return (wide ? Encoding.Unicode : Encoding.ASCII).GetString(bytes.ToArray());
        }

        private static byte[] Read(IntPtr handle, long address, int count)
        {
            if (address < 0x10000 || address > 0x7FFFFFFFFFFF || count < 1 || count > 65536) { throw new InvalidDataException("Invalid read bounds."); }
            var data = new byte[count];
            if (!ReadProcessMemory(handle, new IntPtr(address), data, (nuint)count, out var read) || read != (nuint)count) { throw new Win32Exception(Marshal.GetLastWin32Error(), $"Read at 0x{address:X}"); }
            return data;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
