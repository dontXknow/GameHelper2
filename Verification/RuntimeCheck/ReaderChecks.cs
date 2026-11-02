// <copyright file="ReaderChecks.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace RuntimeCheck
{
    using System.Collections.Concurrent;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Runtime.InteropServices;
    using System.Runtime.Loader;
    using System.Text;
    using GameOffsets.Natives;
    using GameOffsets.Objects.Components;
    using GameOffsets.Objects.States.InGameState;
    using GameOffsets.Objects.UiElement;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    internal static partial class Program
    {
        private const BindingFlags HiddenInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private static void ReaderTests(string evidence)
        {
            evidence = Path.GetFullPath(evidence);
            using var session = new ReaderSession(Environment.ProcessId, evidence);
            using var buffers = new Buffers();
            var results = new List<string>();
            var mapReader = session.Reader.GetType().GetMethod("ReadStdMap", HiddenInstance)!
                .MakeGenericMethod(typeof(EntityNodeKey), typeof(EntityNodeValue));
            var seen = new ConcurrentBag<uint>();
            Func<EntityNodeKey, EntityNodeValue, bool> callback = (key, _) => { seen.Add(key.id); return true; };
            int Walk(StdMap map)
            {
                seen.Clear();
                return (int)mapReader.Invoke(session.Reader, new object[] { map, 10000, true, callback })!;
            }
            void NoCallbacks(StdMap map, string label)
            {
                if (Walk(map) != 0 || seen.Count != 0) { throw new InvalidDataException(label + ": invalid root reached callback."); }
                results.Add(label + " passed");
            }

            NoCallbacks(new StdMap { Size = 7 }, "Null map header");
            var headAddress = buffers.Alloc(Unsafe.SizeOf<StdMapNode<EntityNodeKey, EntityNodeValue>>());
            var head = new StdMapNode<EntityNodeKey, EntityNodeValue> { IsNil = true };
            buffers.Put(headAddress, head);
            var map = new StdMap { Head = headAddress, Size = 7 };
            NoCallbacks(map, "Null root under valid header");
            head.Parent = headAddress; buffers.Put(headAddress, head);
            NoCallbacks(map, "Header points back to itself");
            var nodes = Enumerable.Range(0, 127).Select(_ => buffers.Alloc(Unsafe.SizeOf<StdMapNode<EntityNodeKey, EntityNodeValue>>())).ToArray();
            head.Parent = nodes[0]; buffers.Put(headAddress, head);
            var first = new StdMapNode<EntityNodeKey, EntityNodeValue> { Color = 4, Left = headAddress, Right = headAddress };
            buffers.Put(nodes[0], first);
            NoCallbacks(map, "Invalid root color");
            first.Color = 0; buffers.Put(nodes[0], first);
            NoCallbacks(map, "Root has no backlink to its header");
            for (var i = 0; i < nodes.Length; i++)
            {
                var node = new StdMapNode<EntityNodeKey, EntityNodeValue>
                {
                    Left = 2 * i + 1 < nodes.Length ? nodes[2 * i + 1] : headAddress,
                    Right = 2 * i + 2 < nodes.Length ? nodes[2 * i + 2] : headAddress,
                    Parent = i == 0 ? headAddress : nodes[(i - 1) / 2],
                    Data = new StdMapNodeData<EntityNodeKey, EntityNodeValue> { Key = new EntityNodeKey { id = (uint)i + 1 }, Value = new EntityNodeValue { EntityPtr = nodes[i] } },
                };
                buffers.Put(nodes[i], node);
            }
            map.Size = nodes.Length;
            Walk(map);
            if (seen.Count != 127 || seen.Distinct().Count() != 127) { throw new InvalidDataException("Parallel map traversal lost or duplicated nodes."); }
            results.Add("127-node map traversed once per node, including parallel subtrees");
            var brokenChild = ReadStruct<StdMapNode<EntityNodeKey, EntityNodeValue>>(((SafeHandle)session.Reader).DangerousGetHandle(), nodes[1].ToInt64());
            brokenChild.Parent = headAddress; buffers.Put(nodes[1], brokenChild);
            Walk(map);
            if (seen.Count != 64 || seen.Contains(2u) || !seen.Contains(3u)) { throw new InvalidDataException("Bad child backlink reached callbacks or removed its valid sibling."); }
            results.Add("Wrong child backlink rejects only its branch and preserves the valid sibling subtree");
            first = new StdMapNode<EntityNodeKey, EntityNodeValue> { Parent = headAddress, Data = new StdMapNodeData<EntityNodeKey, EntityNodeValue> { Key = new EntityNodeKey { id = 1 } } };
            buffers.Put(nodes[0], first); map.Size = 1; Walk(map);
            if (seen.Count != 1) { throw new InvalidDataException("Partial tree did not preserve valid root."); }
            results.Add("Null child links skipped; valid root callback preserved");
            first.Left = new IntPtr(0xAAAC15A3); buffers.Put(nodes[0], first); Walk(map);
            if (seen.Count != 1 || session.DiagnosticKeys != 0) { throw new InvalidDataException("Misaligned node was treated as a valid map node."); }
            results.Add("Misaligned map-node value rejected without reading it");
            first.Left = new IntPtr(0x50000000000); buffers.Put(nodes[0], first); Walk(map);
            if (session.DiagnosticKeys == 0) { throw new InvalidDataException("Genuine failed reads were suppressed."); }
            results.Add("Genuine aligned-but-unreadable child still produces failed-read diagnostics");
            session.ClearDiagnostics();

            foreach (var text in new[] { "Gold", "ÄÖß", "Occultist Mantle", "Gargantuan Mana Flask", "" })
            {
                var row = buffers.Alloc(96);
                var value = new StdWString { Length = text.Length, Capacity = text.Length < 8 ? 7 : text.Length | 7 };
                if (text.Length < 8)
                {
                    var inline = new byte[16]; Encoding.Unicode.GetBytes(text).CopyTo(inline, 0);
                    value.Buffer = new IntPtr(BitConverter.ToInt64(inline));
                    value.ReservedBytes = new IntPtr(BitConverter.ToInt64(inline, 8));
                }
                else { value.Buffer = buffers.Text(text); }
                buffers.Put(row + BaseOffsets.DisplayNameOffset, value);
                var address = buffers.Alloc(Unsafe.SizeOf<BaseOffsets>());
                buffers.Put(address, new BaseOffsets { DisplayNameRowPtr = row });
                var actual = session.BaseName(address);
                if (actual != text) { throw new InvalidDataException($"Base-name mismatch: expected '{text}', got '{actual}'."); }
            }
            results.Add("Production Base reader: inline Gold/Unicode, heap names, and empty name passed");

            var lootAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "Plugins", "LootValue", "LootValue.dll"));
            var lootType = lootAssembly.GetType("LootValue.LootValueCore", true)!;
            var loot = Activator.CreateInstance(lootType)!;
            if (!(bool)lootType.GetMethod("EnsureReflection", HiddenInstance)!.Invoke(loot, null)!) { throw new InvalidDataException("LootValue reader initialization failed."); }
            var readUiText = lootType.GetMethod("ReadUiElementText", HiddenInstance)!;
            var uiAddress = buffers.Alloc(0x400);
            var uiTextOffset = (int)lootType.GetField("UiElementTextOffset", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            foreach (var uiText in new[] { "Gargantuan Mana Flask", "", "Gold" })
            {
                var value = new StdWString { Length = uiText.Length, Capacity = uiText.Length < 8 ? 7 : uiText.Length | 7 };
                if (uiText.Length < 8)
                {
                    var inline = new byte[16]; Encoding.Unicode.GetBytes(uiText).CopyTo(inline, 0);
                    value.Buffer = new IntPtr(BitConverter.ToInt64(inline));
                    value.ReservedBytes = new IntPtr(BitConverter.ToInt64(inline, 8));
                }
                else { value.Buffer = buffers.Text(uiText); }
                buffers.Put(uiAddress + uiTextOffset, value);
                if ((string)readUiText.Invoke(loot, new object[] { uiAddress })! != uiText) { throw new InvalidDataException("LootValue UI text transition failed."); }
            }
            buffers.Put(uiAddress + uiTextOffset, new StdWString { Length = 12, Capacity = 15, Buffer = IntPtr.Zero });
            if ((string)readUiText.Invoke(loot, new object[] { uiAddress })! != "" || session.DiagnosticKeys != 0) { throw new InvalidDataException("LootValue missing UI buffer produced a failed read."); }
            results.Add("Original LootValue plugin reads long/inline UI text and empty transitions; missing heap buffer returns empty text without a failed read");

            var assembly = session.Reader.GetType().Assembly;
            using var fixtureProcess = System.Diagnostics.Process.GetCurrentProcess();
            var coreProcess = assembly.GetType("GameHelper.Core", true)!.GetProperty("Process")!.GetValue(null)!;
            coreProcess.GetType().GetProperty("Information", HiddenInstance)!.SetValue(coreProcess, fixtureProcess);
            var validateItem = assembly.GetType("GameHelper.Plugin.PluginUiElementReflection", true)!.GetMethod("TryValidateItemAddress")!;
            var candidateAddress = buffers.Alloc(Unsafe.SizeOf<ItemStruct>());
            var candidate = new ItemStruct { VTablePtr = fixtureProcess.MainModule!.BaseAddress };
            buffers.Put(candidateAddress, candidate);
            bool ValidateCandidate()
            {
                return (bool)validateItem.Invoke(null, new object[] { candidateAddress, "", "" })!;
            }
            if (ValidateCandidate() || session.DiagnosticKeys != 0) { throw new InvalidDataException("Ritual item candidate with null metadata was read or accepted."); }
            candidate.EntityDetailsPtr = new IntPtr(0x50000000000); buffers.Put(candidateAddress, candidate);
            if (ValidateCandidate() || session.DiagnosticKeys == 0) { throw new InvalidDataException("Real item-metadata read failures were suppressed."); }
            session.ClearDiagnostics();
            var rewardPath = "Metadata/Items/Test/RitualReward";
            var rewardDetails = buffers.Alloc(Unsafe.SizeOf<EntityDetails>());
            buffers.Put(rewardDetails, new EntityDetails { name = new StdWString { Buffer = buffers.Text(rewardPath), Length = rewardPath.Length, Capacity = rewardPath.Length | 7 } });
            candidate.EntityDetailsPtr = rewardDetails; buffers.Put(candidateAddress, candidate);
            var validationArguments = new object[] { candidateAddress, "", "" };
            if (!(bool)validateItem.Invoke(null, validationArguments)! || (string)validationArguments[1] != rewardPath) { throw new InvalidDataException("Valid Ritual item candidate was rejected."); }

            // Exercise the unchanged plugin's real Ritual resolver over a small UI tree.
            var ritualPath = (int[])lootType.GetField("RitualRewardGridPath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            var slotOffset = (int)lootType.GetField("UiElementItemAddressOffset", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            IntPtr UiNode(params IntPtr[] children)
            {
                var address = buffers.Alloc(Math.Max(0x600, Unsafe.SizeOf<UiElementBaseOffset>()));
                var array = buffers.Alloc(Math.Max(8, children.Length * 8));
                for (var i = 0; i < children.Length; i++) { buffers.Put(array + i * 8, children[i]); }
                buffers.Put(address, new UiElementBaseOffset { Self = address, Flags = 1u << 11, ChildrensPtr = new StdVector { First = array, Last = array + children.Length * 8, End = array + children.Length * 8 } });
                return address;
            }
            var placeholderAddress = buffers.Alloc(Unsafe.SizeOf<ItemStruct>());
            buffers.Put(placeholderAddress, new ItemStruct { VTablePtr = fixtureProcess.MainModule.BaseAddress });
            var emptyTile = UiNode(); buffers.Put(emptyTile + slotOffset, placeholderAddress);
            var readyTile = UiNode(); buffers.Put(readyTile + slotOffset, candidateAddress);
            var rewardGrid = UiNode(emptyTile, readyTile);
            var uiRoot = rewardGrid;
            for (var i = ritualPath.Length - 1; i >= 0; i--)
            {
                var children = new IntPtr[ritualPath[i] + 1]; children[ritualPath[i]] = uiRoot;
                uiRoot = UiNode(children);
            }
            var resolveRitual = lootType.GetMethod("ResolveVisibleRitualRewardGrid", HiddenInstance)!;
            if ((IntPtr)resolveRitual.Invoke(loot, new object[] { uiRoot })! != rewardGrid) { throw new InvalidDataException("Valid Ritual grid was not found after skipping a placeholder."); }
            candidate.EntityDetailsPtr = IntPtr.Zero; buffers.Put(candidateAddress, candidate);
            if ((IntPtr)resolveRitual.Invoke(loot, new object[] { uiRoot })! != IntPtr.Zero) { throw new InvalidDataException("Ritual grid containing only placeholders was accepted."); }
            candidate.EntityDetailsPtr = rewardDetails; buffers.Put(candidateAddress, candidate);
            if ((IntPtr)resolveRitual.Invoke(loot, new object[] { uiRoot })! != rewardGrid || session.DiagnosticKeys != 0) { throw new InvalidDataException("Ritual grid failed to recover or generated null metadata reads."); }
            results.Add("Original LootValue Ritual resolver skips unready item metadata, keeps valid rewards, and recovers when metadata appears; genuine nonzero failures stay diagnosed");

            var entityType = assembly.GetType("GameHelper.RemoteObjects.States.InGameStateObjects.Entity", true)!;
            var badDetails = new IntPtr(0x48000400000000);
            var entityBuffer = buffers.Alloc(Unsafe.SizeOf<EntityOffsets>());
            buffers.Put(entityBuffer, new EntityOffsets { Id = 42, IsValid = 12, ItemBase = new ItemStruct { EntityDetailsPtr = badDetails } });
            var entityObject = Activator.CreateInstance(entityType, nonPublic: true)!;
            entityType.GetProperty("Address")!.SetValue(entityObject, entityBuffer);
            if ((bool)entityType.GetProperty("IsValid")!.GetValue(entityObject)!) { throw new InvalidDataException("Entity with failed component metadata stayed valid."); }
            results.Add("Entity with impossible metadata pointer is invalidated before metadata read");

            var itemType = assembly.GetType("GameHelper.RemoteObjects.States.InGameStateObjects.Item", true)!;
            var itemBuffer = buffers.Alloc(Unsafe.SizeOf<ItemStruct>());
            buffers.Put(itemBuffer, new ItemStruct { EntityDetailsPtr = badDetails });
            var itemObject = Activator.CreateInstance(itemType, HiddenInstance, null, new object[] { IntPtr.Zero }, null)!;
            var addressProperty = itemType.GetProperty("Address")!;
            addressProperty.SetValue(itemObject, itemBuffer);
            if ((bool)itemType.GetProperty("IsValid")!.GetValue(itemObject)!) { throw new InvalidDataException("Item with failed metadata stayed valid."); }
            foreach (var path in new[] { "Metadata/Items/Test/First", "Metadata/Items/Test/Reused" })
            {
                var lookup = buffers.Alloc(Unsafe.SizeOf<ComponentLookUpStruct>());
                var details = buffers.Alloc(Unsafe.SizeOf<EntityDetails>());
                buffers.Put(details, new EntityDetails { ComponentLookUpPtr = lookup, name = new StdWString { Buffer = buffers.Text(path), Length = path.Length, Capacity = path.Length | 7 } });
                buffers.Put(itemBuffer, new ItemStruct { EntityDetailsPtr = details });
                addressProperty.SetValue(itemObject, itemBuffer);
                if (!(bool)itemType.GetProperty("IsValid")!.GetValue(itemObject)! || (string)itemType.GetProperty("Path")!.GetValue(itemObject)! != path) { throw new InvalidDataException("Item did not recover or retained old metadata at reused address."); }
            }
            results.Add("Item recovers at the same address; changed metadata replaces cached path/components");
            var areaType = assembly.GetType("GameHelper.RemoteObjects.States.InGameStateObjects.AreaInstance", true)!;
            var areaObject = Activator.CreateInstance(areaType, HiddenInstance, null, new object[] { IntPtr.Zero }, null)!;
            var tileBuffer = buffers.Alloc(Unsafe.SizeOf<TileStructure>());
            var terrain = new TerrainStruct
            {
                TotalTiles = new StdTuple2D<long>(1, 1),
                TileDetailsPtr = new StdVector { First = tileBuffer, Last = tileBuffer + Unsafe.SizeOf<TileStructure>(), End = tileBuffer + Unsafe.SizeOf<TileStructure>() },
            };
            areaType.GetProperty("TerrainMetadata")!.SetValue(areaObject, terrain);
            var getTgt = areaType.GetMethod("GetTgtFileData", HiddenInstance)!;
            var getHeight = areaType.GetMethod("GetTerrainHeight", HiddenInstance)!;
            var tgtResult = (System.Collections.IDictionary)getTgt.Invoke(areaObject, null)!;
            var heightResult = (float[][])getHeight.Invoke(areaObject, null)!;
            if (tgtResult.Count != 0 || heightResult.Length != 23 || heightResult.Any(row => row.Length != 23 || row.Any(v => v != 0))) { throw new InvalidDataException("Missing optional terrain pointers changed terrain output."); }
            var tgtAddress = buffers.Alloc(Unsafe.SizeOf<TgtFileStruct>());
            const string tgtPath = "Metadata/Terrain/Test/Test.tdt";
            buffers.Put(tgtAddress, new TgtFileStruct { TgtPath = new StdWString { Buffer = buffers.Text(tgtPath), Length = tgtPath.Length, Capacity = tgtPath.Length | 7 } });
            buffers.Put(tileBuffer, new TileStructure { TgtFilePtr = tgtAddress });
            tgtResult = (System.Collections.IDictionary)getTgt.Invoke(areaObject, null)!;
            if (tgtResult.Count != 1 || !tgtResult.Contains(tgtPath + "x:0-y:0")) { throw new InvalidDataException("Valid terrain file was skipped."); }
            results.Add("Missing terrain pointers preserve empty/flat output; valid terrain file is still included");
            if (session.DiagnosticKeys != 0) { throw new InvalidDataException("Fixture reads unexpectedly created diagnostic failures."); }
            results.Add("No failed-read diagnostics generated by the valid or absent-root fixtures");
            File.WriteAllLines(Path.Combine(evidence, "reader-test-results.txt"), results);
            foreach (var result in results) { Console.WriteLine(result); }
        }

        private static void BaseLive(int processId, string evidence)
        {
            evidence = Path.GetFullPath(evidence);
            var probe = JObject.Parse(File.ReadAllText(Path.Combine(evidence, "entity-probe.json")));
            var items = probe.SelectTokens("$.Structures.AwakeMap.ItemSamples[*]").ToArray();
            using var session = new ReaderSession(processId, evidence);
            var results = new List<object>();
            var handle = ((SafeHandle)session.Reader).DangerousGetHandle();
            foreach (var item in items)
            {
                var address = Convert.ToInt64(item["Address"]!.Value<string>()![2..], 16);
                // Item allocations are reused as the user plays. Compare both readers
                // on the current string instead of requiring an old captured name.
                var data = ReadStruct<BaseOffsets>(handle, address);
                var fieldAddress = data.DisplayNameRowPtr.ToInt64() + BaseOffsets.DisplayNameOffset;
                var value = ReadStruct<StdWString>(handle, fieldAddress);
                var expected = ReadWString(handle, fieldAddress, value);
                var actual = session.BaseName(new IntPtr(address));
                if (actual != expected) { throw new InvalidDataException($"Live item changed or reader mismatch at 0x{address:X}: '{expected}' vs '{actual}'."); }
                results.Add(new { Address = $"0x{address:X}", Expected = expected, Actual = actual });
            }
            var areaAddress = Convert.ToInt64(probe["Structures"]!["AreaInstance"]!.Value<string>()![2..], 16);
            var area = ReadStruct<AreaInstanceOffsets>(handle, areaAddress);
            var mapReader = session.Reader.GetType().GetMethod("ReadStdMap", HiddenInstance)!
                .MakeGenericMethod(typeof(EntityNodeKey), typeof(EntityNodeValue));
            var maps = new List<object>();
            string? liveGold = null;
            var goldFile = Path.Combine(evidence, "gold-field-observation.json");
            if (File.Exists(goldFile))
            {
                var goldRow = Convert.ToInt64(JObject.Parse(File.ReadAllText(goldFile))["Row"]!.Value<string>()![2..], 16);
                var goldValue = ReadStruct<StdWString>(handle, goldRow + BaseOffsets.DisplayNameOffset);
                liveGold = (string)session.Reader.GetType().GetMethod("ReadStdWString", HiddenInstance)!.Invoke(session.Reader, new object[] { goldValue })!;
                if (liveGold != "Gold") { throw new InvalidDataException("Gold row changed or production string reader failed."); }
            }
            foreach (var (label, map) in new[] { ("Awake", area.Entities.AwakeEntities), ("Sleeping", area.Entities.SleepingEntities) })
            {
                int callbacks = 0, matchingIds = 0, failures = 0;
                Func<EntityNodeKey, EntityNodeValue, bool> callback = (key, value) =>
                {
                    Interlocked.Increment(ref callbacks);
                    try
                    {
                        var entity = ReadStruct<EntityOffsets>(handle, value.EntityPtr.ToInt64());
                        if (entity.Id == key.id) { Interlocked.Increment(ref matchingIds); }
                        else { Interlocked.Increment(ref failures); }
                    }
                    catch { Interlocked.Increment(ref failures); }
                    return true;
                };
                mapReader.Invoke(session.Reader, new object[] { map, 500000, true, callback });
                if (callbacks != map.Size || failures != 0) { throw new InvalidDataException($"Live map changed or validation failed ({label}): expected={map.Size}, read={callbacks}, failures={failures}"); }
                maps.Add(new { Map = label, Expected = map.Size, Read = callbacks, MatchingIds = matchingIds, Failures = failures });
            }
            if (session.DiagnosticKeys != 0) { throw new InvalidDataException("Production Base reader generated failed-read diagnostics."); }
            File.WriteAllText(Path.Combine(evidence, "base-live-results.json"), JsonConvert.SerializeObject(new { processId, Checked = results.Count, DiagnosticKeys = session.DiagnosticKeys, LiveGold = liveGold, Items = results, Maps = maps }, Formatting.Indented));
            Console.WriteLine($"Production Base reader: {results.Count} live item names match; captured Gold row decoded as '{liveGold ?? "not checked"}'; production map reader: both full trees match their entity IDs; zero failed-read diagnostic entries.");
        }

        private sealed class ReaderSession : IDisposable
        {
            private readonly string oldDirectory = Environment.CurrentDirectory;
            private readonly Type baseType;
            private readonly object statistics;
            internal object Reader { get; }
            internal int DiagnosticKeys => (int)this.statistics.GetType().GetProperty("Count")!.GetValue(this.statistics)!;
            internal void ClearDiagnostics() => this.statistics.GetType().GetMethod("Clear")!.Invoke(this.statistics, null);

            internal ReaderSession(int processId, string evidence)
            {
                var isolated = Path.Combine(evidence, "ReaderTestIsolation");
                Directory.CreateDirectory(isolated); Environment.CurrentDirectory = isolated;
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, "GameHelper.dll"));
                var core = assembly.GetType("GameHelper.Core", true)!;
                var settings = core.GetProperty("GHSettings")!.GetValue(null)!;
                settings.GetType().GetField("ShowMemoryDiagnostics")!.SetValue(settings, true);
                var readerType = assembly.GetType("GameHelper.Utils.SafeMemoryHandle", true)!;
                this.Reader = Activator.CreateInstance(readerType, HiddenInstance, null, new object[] { processId }, null)!;
                var process = core.GetProperty("Process")!.GetValue(null)!;
                process.GetType().GetProperty("Handle", HiddenInstance)!.SetValue(process, this.Reader);
                this.baseType = assembly.GetType("GameHelper.RemoteObjects.Components.Base", true)!;
                var diagnostics = assembly.GetType("GameHelper.Ui.MemoryReadDiagnostics", true)!;
                this.statistics = diagnostics.GetField("Stats", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                this.statistics.GetType().GetMethod("Clear")!.Invoke(this.statistics, null);
            }

            internal string BaseName(IntPtr address)
            {
                var instance = Activator.CreateInstance(this.baseType, new object[] { IntPtr.Zero })!;
                this.baseType.GetProperty("Address")!.SetValue(instance, address);
                return (string)this.baseType.GetProperty("BaseItemName")!.GetValue(instance)!;
            }

            public void Dispose()
            {
                ((IDisposable)this.Reader).Dispose(); Environment.CurrentDirectory = this.oldDirectory;
            }
        }

        private sealed class Buffers : IDisposable
        {
            private readonly List<IntPtr> allocations = new();
            internal IntPtr Alloc(int size)
            {
                var address = Marshal.AllocHGlobal(size); this.allocations.Add(address);
                Marshal.Copy(new byte[size], 0, address, size); return address;
            }
            internal IntPtr Text(string text)
            {
                var address = Marshal.StringToHGlobalUni(text); this.allocations.Add(address); return address;
            }
            internal void Put<T>(IntPtr address, T value) where T : unmanaged
            {
                var bytes = new byte[Unsafe.SizeOf<T>()]; MemoryMarshal.Write(bytes, in value);
                Marshal.Copy(bytes, 0, address, bytes.Length);
            }
            public void Dispose() { foreach (var address in this.allocations) { Marshal.FreeHGlobal(address); } }
        }
    }
}
