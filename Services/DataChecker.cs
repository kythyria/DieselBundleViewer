using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.IO.Packaging;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;
using CSScriptLib;
using DieselBundleViewer.Models;
using DieselEngineFormats.Bundle;
using ProgressRecord = DieselBundleViewer.ViewModels.ProgressRecord;

namespace DieselBundleViewer.Services
{
    static class DataChecker
    {
        public class CheckedFile
        {
            public FileEntry File { get; set; }
            public PackageFileEntry PackageEntry { get; set; }
            public byte[] Hash { get; set; }
        }

        public class Report
        {
            /// <summary>
            /// Package entries by filename, such that not all of them share a hash.
            /// </summary>
            public Dictionary<string, List<CheckedFile>> DivergingFiles { get; set; }
            /// <summary>
            /// Package entries by hash, such that not all of the package entries for a given hash share a path.
            /// </summary>
            public Dictionary<byte[], List<CheckedFile>> CrossPathDuplicates { get; set; }
        }

        public static async Task<Report> Analyse(IEnumerable<FileEntry> files, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            progress.Report(new ProgressRecord("Thinking", 0, 0));

            var worklist = files.Where(file => true)
                .SelectMany<FileEntry, (FileEntry File, PackageFileEntry PFE)>(file => file.BundleEntries.Select(be => (file, be.Value)))
                .GroupBy(be => be.PFE.Parent.BundleName)
                .ToImmutableDictionary(t => t.Key, t => t.OrderBy(pe => pe.PFE.Address).ToImmutableArray());

            int total = worklist.Count + 1;
            int done = 0;
            var results = new List<CheckedFile>();

            foreach (var (bundleName, entries) in worklist)
            {
                progress.Report(new ProgressRecord("Calculating hashes", total, done));
                if (ct.IsCancellationRequested) { break; }

                var bundle_path = Path.Combine(Utils.CurrentWindow.AssetsDir, bundleName + ".bundle");
                using var stream = new FileStream(bundle_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);

                var hashTasks = new List<Task<CheckedFile>>();

                foreach(var (fileEntry, packageEntry) in entries)
                {
                    stream.Position = packageEntry.Address;
                    var actualLength = packageEntry.Length == -1 ? stream.Length - stream.Position : packageEntry.Length;
                    var bytes = new byte[actualLength];

                    var actuallyRead = await stream.ReadAsync(bytes, 0, (int)actualLength, ct);
                    if (actuallyRead < actualLength && !ct.IsCancellationRequested)
                    {
                        Console.WriteLine("Read too short in bundle {0} for file {1}", packageEntry.Parent.Name, fileEntry.EntryPath);
                    }

                    if(ct.IsCancellationRequested) { break; }

                    hashTasks.Add(Task.Run(() =>
                    {
                        using var hasher = SHA1.Create();
                        var hash = hasher.ComputeHash(bytes);
                        return new CheckedFile() { File = fileEntry, PackageEntry = packageEntry, Hash = hash };
                    }));
                }
                results.AddRange(await Task.WhenAll(hashTasks));

                done++;
            }

            progress.Report(new ProgressRecord("Detecting oddities", total, done));

            var report = new Report();

            report.DivergingFiles = results.GroupBy(cf => cf.File.EntryPath)
                .Where(g => g.Select(cf => cf.Hash).Distinct(HashByteComparer.Instance).Count() > 1)
                .ToDictionary(g => g.Key, g => g.ToList());

            report.CrossPathDuplicates = results.GroupBy(cf => cf.Hash, HashByteComparer.Instance)
                .Where(g => g.Select(cf => cf.File.EntryPath).Distinct().Count() > 1)
                .ToDictionary(g => g.Key, g => g.ToList(), HashByteComparer.Instance);

            return report;
        }

        public static async Task Analyse(IEnumerable<FileEntry> files, string outputPath, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var result = await Analyse(files, progress, ct);
            if (ct.IsCancellationRequested) { return; }

            using var outfile = new StreamWriter(outputPath, false, new UTF8Encoding());

            if (result.DivergingFiles.Count > 0)
            {
                outfile.WriteLine("Same path, different hashes:");
                foreach (var (path, filelist) in result.DivergingFiles)
                {
                    outfile.WriteLine("    {0}", path);
                    foreach (var cf in filelist)
                    {
                        outfile.WriteLine("        {0}: {1}", BitConverter.ToString(cf.Hash).Replace("-", ""), cf.PackageEntry.PackageName);
                    }
                }
                outfile.WriteLine();

                outfile.WriteLine("Different path, same hashes:");
                foreach (var (hash, filelist) in result.CrossPathDuplicates)
                {
                    if(filelist[0].PackageEntry.Length == 0) { continue; }
                    outfile.WriteLine("    {0}", BitConverter.ToString(hash).Replace("-", ""));
                    foreach (var group in filelist.GroupBy(cf => cf.PackageEntry.PackageName.ToString()))
                    {
                        outfile.WriteLine("        {0}", group.Key);
                        foreach (var cf in group)
                        {
                            outfile.WriteLine("            {0}", cf.File.EntryPath);
                        }
                    }
                }
            }
            else
            {
                outfile.WriteLine("Different path, same hashes:");
                foreach (var (hash, filelist) in result.CrossPathDuplicates)
                {
                    outfile.WriteLine("    {0}", BitConverter.ToString(hash).Replace("-", ""));
                    filelist.Select(i => i.File.EntryPath)
                        .Distinct()
                        .OrderBy(i => i)
                        .ForEach(i => outfile.WriteLine("        {0}", i));
                }
            }
            await outfile.FlushAsync();
        }

        // HashByte because the implementation of GetHashCode expects hash-like
        // distribution of the first four bytes.
        class HashByteComparer : IEqualityComparer<byte[]>
        {
            public static readonly HashByteComparer Instance = new HashByteComparer();
            public bool Equals([AllowNull] byte[] x, [AllowNull] byte[] y)
            {
                if((x == null) && (y == null)) { return true; }
                if((x == null) != (y == null)) { return false; }
                if(x.Length != y.Length) { return false; }
                return Enumerable.SequenceEqual(x, y);
            }

            public int GetHashCode([DisallowNull] byte[] obj)
            {
                int res = 0;
                if (obj.Length > 0) { res = obj[0]; }
                if (obj.Length > 1) { res += obj[1] << 8;  }
                if (obj.Length > 2) { res += obj[2] << 16; }
                if (obj.Length > 3) { res += obj[3] << 24; }
                return res;
            }
        }
    }
}
