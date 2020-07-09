using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.XPath;
using DieselBundleViewer.Models;
using DieselEngineFormats.Bundle;
using DieselEngineFormats.ScriptData;

using ProgressRecord = DieselBundleViewer.ViewModels.ProgressRecord;

namespace DieselBundleViewer.Services
{
    class HashlistGenerator
    {

        public class ExtractionResultRecord
        {
            public ImmutableArray<string> FoundNames { get; private set; }

            public ExtractionResultRecord(ImmutableArray<string> foundNames)
            {
                FoundNames = foundNames;
            }
        }

        private static ImmutableDictionary<string, ImmutableArray<(FileEntry File, PackageFileEntry PackageEntry)>> BuildWorklist(IEnumerable<FileEntry> files)
        {
            HashSet<string> relevantExtensions = new HashSet<string>(FileProcessors.Keys);
            return files.Where(file => relevantExtensions.Contains(file.ExtensionIds.UnHashed))
                .SelectMany<FileEntry, (FileEntry File, PackageFileEntry PFE)>(file => file.BundleEntries.Select(be => (file, be.Value)))
                .GroupBy(be => be.PFE.Parent.BundleName)
                .ToImmutableDictionary(t => t.Key, t => t.OrderBy(pe => pe.PFE.Address).ToImmutableArray());
        }

        private static async Task<IEnumerable<string>> DoExtract(IEnumerable<FileEntry> files, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            progress.Report(new ProgressRecord("Determining reading order", 0, 0));
            var toProcess = BuildWorklist(files);

            var overallResults = new HashSet<string>();

            if (ct.IsCancellationRequested) { return overallResults; }

            int total = toProcess.Count;
            int done = 0;
            foreach (var (bundleName, fileList) in toProcess)
            {
                if (ct.IsCancellationRequested) { return overallResults; }

                progress.Report(new ProgressRecord($"Scanning bundles for names", total, done));
                done++;

                string bundle_path = Path.Combine(Utils.CurrentWindow.AssetsDir, bundleName + ".bundle");
                if (!File.Exists(bundle_path))
                {
                    Console.WriteLine("Bundle: {0}, does not exist!", bundle_path);
                    continue;
                }

                var pendingResults = new List<Task<IEnumerable<string>>>();

                using var fs = new FileStream(bundle_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

                foreach (var (file, packageEntry) in fileList)
                {
                    if (packageEntry.Length != 0)
                    {
                        pendingResults.Add(AnalyzeFileInPackage(file, packageEntry, fs, ct));
                    }
                    else
                    {
                        pendingResults.Add(Task.FromResult(Enumerable.Empty<string>()));
                    }
                }
                var results = await Task.WhenAll(pendingResults);
                overallResults.UnionWith(results.SelectMany(i => i));
            }
            return overallResults;
        }

        private static async Task<IEnumerable<string>> AnalyzeFileInPackage(FileEntry file, PackageFileEntry packageEntry, FileStream fs, CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
            {
                return Enumerable.Empty<string>();
            }

            fs.Position = packageEntry.Address;
            var actualLength = packageEntry.Length == -1 ? fs.Length - fs.Position : packageEntry.Length;
            var bytes = new byte[actualLength];

            var actuallyRead = await fs.ReadAsync(bytes, 0, (int)actualLength, ct);
            if (actuallyRead < actualLength && !ct.IsCancellationRequested)
            {
                Console.WriteLine("Read too short in bundle {0} for file {1}", packageEntry.Parent.Name, file.EntryPath);
            }

            if (ct.IsCancellationRequested)
            {
                return Enumerable.Empty<string>();
            }

            if (FileProcessors.TryGetValue(file.ExtensionIds.ToString(), out var bp))
            {
                try
                {
                    var result = bp(file, packageEntry, bytes);//.Select(i => file.EntryPath + ": " + i);
                    return result;
                }
                catch (Exception e)
                {
                    Console.WriteLine("{0} : {1} Analysis failure: {2}", packageEntry.Parent.Name, file.EntryPath, e.Message);
                    return Enumerable.Empty<string>();
                }
            }
            else
            {
                throw new Exception($"Tried to analyze a file with an unprocessable extension {file.ExtensionIds}");
            }
        }

        public Task<IEnumerable<string>> Extract(IEnumerable<FileEntry> files, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            return HashlistGenerator.DoExtract(files, progress, ct);
        }

        delegate IEnumerable<string> Processor<T>(FileEntry fe, PackageFileEntry pfe, T data);
        delegate IEnumerable<string> ByteProcessor(FileEntry fe, PackageFileEntry pfe, byte[] data);
        delegate IEnumerable<string> ScriptDataProcessor(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> data);

        private static ByteProcessor ProcessXpath(params string[] expressions)
        {
            var strexpr = string.Join(" | ", expressions);
            var expr = XPathExpression.Compile(strexpr);
            return delegate (FileEntry fe, PackageFileEntry pfe, byte[] data)
            {
                using var ms = new MemoryStream(data);
                var xd = new XPathDocument(ms);
                var nodes = xd.CreateNavigator().Select(expr);
                return nodes.Cast<XPathNavigator>().Select(i => i.Value);
            };
        }

        private static ByteProcessor XmlProcessor(Processor<XPathNavigator> proc)
        {
            return delegate (FileEntry fe, PackageFileEntry pfe, byte[] data)
            {
                using var ms = new MemoryStream(data);
                var xd = new XPathDocument(ms);
                return proc(fe, pfe, xd.CreateNavigator());
            };
        }

        private static ByteProcessor ProcessScriptData(ScriptDataProcessor processor)
        {
            return delegate (FileEntry fe, PackageFileEntry pfe, byte[] data)
            {
                using var mr = new MemoryStream(data);
                using var br = new BinaryReader(mr);
                var sd = new ScriptData(br, Utils.IsRaid());
                var root = sd.Root as Dictionary<string, object>;

                return processor(fe, pfe, root);
            };
        }

        private static Dictionary<string, ByteProcessor> FileProcessors = new Dictionary<string, ByteProcessor> {
            //{ "unit", XmlProcessor(ProcessUnit) },
            { "unit", ProcessXpath(
                "/unit/anim_state_machine/@name | /unit/object/@file | /unit/network/@remote_unit",
                "/unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear']/@value",
                "/unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear_object']/@value",
                "/unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear_decal_mesh']/@value",
                "/unit/dependencies/depends_on/attribute::*"
            )},
            { "object", ProcessXpath("//@name | //@culling_object | //@default_material | //@file | //@object") },
            { "material_config", ProcessXpath("/materials/@group | /materials/material/@name | //@file") },
            { "merged_font", ProcessXpath("/merged_font/font/@name") },
            { "gui", ProcessXpath("@font_s | @font | //bitmap/@texture_s | //preload/@texture") },
            { "scene", ProcessXpath("//load_scene/@file | //load_scene/@materials | //object/@name") },
            { "animation_def", ProcessXpath("//bone/@name|//subset/@file") },
            { "animation_state_machine", ProcessXpath("//@file") },
            { "animation_subset", ProcessXpath("//@file") },
            { "effect", ProcessXpath("//@texture | //@material_config | //@model | //@object | //@effect") },
            { "continent", ProcessScriptData(ProcessContinent) },
            { "sequence_manager", ProcessScriptData(ProcessSequenceManager) },
            { "world", ProcessScriptData(ProcessWorld) },
            { "environment", ProcessScriptData(ProcessEnvironment) },
            { "dialog_index", ProcessScriptData(ProcessDialogIndex) }
        };

        static readonly XPathExpression UnitFinder = XPathExpression.Compile(
            "/unit/anim_state_machine/@name | /unit/network/@remote_unit"
            + "| /unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear']/@value"
            + "| /unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear_object']/@value"
            + "| /unit/extensions/extension[@class='CopDamage']/var[@name='_head_gear_decal_mesh']/@value"
            + "| /unit/dependencies/depends_on/attribute::*");
        static readonly XPathExpression UnitObjectFinder = XPathExpression.Compile("/unit/object/@file");
        private static IEnumerable<string> ProcessUnit(FileEntry fe, PackageFileEntry pfe, XPathNavigator data)
        {
            var names = data.Select(UnitFinder).Cast<XPathNavigator>().Select(i => i.Value);
            var objectPath = data.Select(UnitObjectFinder).Cast<XPathNavigator>()
                .Select(i => i.Value)
                // TODO: Rearrange everything until these are only generated if they truly exist.
                ;//.AndSelect(i => i.Contains("wpn_fps") ? i + "_npc" : null);
            return Enumerable.Concat(names, objectPath);
        }

        static readonly ImmutableArray<string> InstanceFilenames = ImmutableArray.CreateRange(new string[] {
            "world", "world/world", "continents", "world_cameras", "world_sounds",
            "cover_data", "massunit", "mission", "nav_manager_data"
        });
        private static IEnumerable<string> ProcessContinent(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> root)
        {
            var instanceNames = root.EntryTable("instances").TableChildren()
                .Entry<string>("folder")
                .Select(i => Regex.Replace(i, "/world$", "/"))
                .SelectMany(bp => InstanceFilenames.Select(i => bp + i));

            var ud = root.EntryTable("statics").TableChildren()
                .EntryTable("unit_data");

            return instanceNames
                .Concat(ud.Entry<string>("name"))
                .Concat(ud.EntryTable("editable_gui").Entry<string>("font")
            );
        }

        private static IEnumerable<string> ProcessSequenceManager(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> root)
        {
            return root.Values.WhereMeta("unit").TableChildren()
                .WhereMeta("sequence").TableChildren()
                .WhereMeta("material_config")
                .Entry<string>("name")
                .Select(i => Regex.Match(i, @"^ *('|"")((?:\\\1|(?!\1).)+)\1 * $"))
                .Where(i => i != null && i.Success)
                .Select(m => m.Groups[2].Value);
        }

        private static IEnumerable<string> ProcessWorld(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> root)
        {
            var env = root.EntryTable("environment");
            return ScriptDataQuery.Concat(
                env.EntryTable("environment_areas").TableChildren().Entry<string>("environment"),
                env.EntryTable("environment_values").Entry<string>("environment"),
                env.EntryTable("effects").TableChildren().Entry<string>("name"));
        }

        static readonly ImmutableArray<string> EnvironmentKeys = ImmutableArray.CreateRange(new string[] {
            "global_world_overlay_texture", "global_texture", "global_world_overlay_mask_texture", "underlay"
        });
        private static IEnumerable<string> ProcessEnvironment(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> root)
        {
            return root.TableChildren().WhereMeta("data")
                .TableChildren().WhereMeta("others")
                .TableChildren().Where(i => EnvironmentKeys.Contains(i["key"]))
                .Entry<string>("value");
        }

        private static IEnumerable<string> ProcessDialogIndex(FileEntry fe, PackageFileEntry pfe, Dictionary<string, object> root)
        {
            return root.TableChildren().WhereMeta("include").Entry<string>("name").Select(i => "gamedata/dialogs/" + i);
        }
    }
}
