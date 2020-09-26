using DieselBundleViewer.Models;
using DieselBundleViewer.ViewModels;
using DieselEngineFormats.Utils;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DieselBundleViewer.Services
{
    static class BruteForcer
    {
        internal static async Task SearchForStreams(Dictionary<uint, FileEntry> fileEntries, string outPath, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var s = new System.Diagnostics.Stopwatch();
            s.Start();
            progress.Report(new ProgressRecord("Loading bank/stream names", 0, 0));

            var pendingBankfile = File.ReadAllLinesAsync("Data/banknames", ct);
            var pendingStreamfile = File.ReadAllLinesAsync("Data/streamnames", ct);

            // It's immutable. Very threadsafe.
            var files = ImmutableHashSet.CreateRange(fileEntries.Select(i => i.Value.PathIds.Hashed));

            var interestingBanks = (await pendingBankfile)
                .Where(i => files.Contains(Hash64.HashString(i)))
                .Select(i => i.Split("/").Last())
                .ToList();
            var streamNames = await pendingStreamfile;

            var interestingPaths = new ConcurrentBag<string>();
            int completed = 0;
            int total = streamNames.Length;

            var progressReporter = Task.Run(async () =>
            {
                while (completed < total && !ct.IsCancellationRequested)
                {
                    await Task.Delay(100);
                    progress.Report(new ProgressRecord("Checking stream names", total, completed));
                }
            });

            var opts = new ParallelOptions();
            opts.CancellationToken = ct;
            Parallel.ForEach(streamNames, opts, (s) =>
            {
                for (var i = 0; i < interestingBanks.Count; i++)
                {
                    var path = $"soundbanks/streamed/{interestingBanks[i]}/{s}";
                    var hsh = Hash64.HashString(path);
                    if (files.Contains(hsh))
                        interestingPaths.Add(path);
                }
                Interlocked.Increment(ref completed);
            });

            File.WriteAllLines(outPath, interestingPaths, new UTF8Encoding());

            completed = total;
            s.Stop();
            Console.WriteLine("Bruteforced {1} combinations in {0} ms", s.ElapsedMilliseconds, total * interestingBanks.Count);
        }

        internal static async Task SearchForCubelights(Dictionary<uint, FileEntry> fileEntries, string outPath, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var s = new System.Diagnostics.Stopwatch();
            s.Start();

            progress.Report(new ProgressRecord("Looking for worlds", 0, 0));

            var levels = fileEntries.Values
                .Where(fe => fe.PathIds.HasUnHashed)
                .Where(fe => fe.ExtensionIds.ToString() == "world")
                .Select(fe => fe.Parent.EntryPath)
                .ToList();
            var files = ImmutableHashSet.CreateRange(fileEntries.Select(i => i.Value.PathIds.Hashed));

            var interestingPaths = new ConcurrentBag<string>();
            var total = levels.Count;
            var completed = 0;

            var progressReporter = Task.Run(async () =>
            {
                while (completed < total && !ct.IsCancellationRequested)
                {
                    await Task.Delay(100);
                    progress.Report(new ProgressRecord("Checking cubelight names", total, completed));
                }
            });

            var opts = new ParallelOptions();
            opts.CancellationToken = ct;
            Parallel.ForEach(levels, levelname =>
            {
                for (var i = 1000; i <= 1000000; i++)
                {
                    var path = $"{levelname}/cube_lights/{i}";
                    var hsh = Hash64.HashString(path);
                    if (files.Contains(hsh))
                        interestingPaths.Add(path);
                }
                var domeocc = $"{levelname}/cube_lights/dome_occlusion";
                if (files.Contains(Hash64.HashString(domeocc)))
                    interestingPaths.Add(domeocc);
                Interlocked.Increment(ref completed);
            });

            File.WriteAllLines(outPath, interestingPaths, new UTF8Encoding());

            completed = total;
            s.Stop();

            Console.WriteLine("Bruteforced {1} combinations in {0} ms", s.ElapsedMilliseconds, total * 999000);
            await progressReporter;
        }

        private static readonly string[][] SuffixPieces = new string[][]
        {
            new string[] { " - Copy ", " copy ", "copy ", "-", " ", "" },
            new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
                "(0)", "(1)", "(2)", "(3)", "(4)", "(5)", "(6)", "(7)", "(8)", "(9)", "" }
        };

        internal static async Task SearchForSuffixed(Dictionary<uint, FileEntry> fileEntries, string outPath, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var s = new System.Diagnostics.Stopwatch();
            s.Start();

            var suffixEnum = SuffixPieces.CartesianProduct().Select(combo => string.Join("", combo));
            var suffixes = suffixEnum.ToList();

            var paths = new List<string[]>();
            foreach(var fe in fileEntries.Values)
            {
                var path = fe.PathIds.UnHashed;
                var split = path.Split('/');
                var rejoined = new string[] {
                    string.Join('/', split.Take(split.Length - 1)),
                    split[^1]
                };
                if (rejoined.Length == 2) paths.Add(rejoined);
                if (rejoined.Length > 2) throw new Exception(path);
            }

            var files = ImmutableHashSet.CreateRange(fileEntries.Select(i => i.Value.PathIds.Hashed));

            var total = paths.Count;
            var completed = 0;

            var progressReporter = Task.Run(async () =>
            {
                while (completed < total && !ct.IsCancellationRequested)
                {
                    await Task.Delay(100);
                    progress.Report(new ProgressRecord("Scanning for copies", total, completed));
                }
            });

            var interestingPaths = new ConcurrentBag<string>();
            var opts = new ParallelOptions();
            opts.CancellationToken = ct;

            Parallel.ForEach(paths, path =>
            {
                foreach(var a in suffixes)
                {
                    foreach(var b in suffixes)
                    {
                        var candidate = path[0] + a + "/" + path[1] + b;
                        if (a != "" && b != "" && files.Contains(Hash64.HashString(candidate)))
                        {
                            Console.WriteLine(candidate);
                            interestingPaths.Add(candidate);
                        }

                    }
                }
                Interlocked.Increment(ref completed);
            });

            File.WriteAllLines(outPath, interestingPaths, new UTF8Encoding());

            completed = total;
            s.Stop();

            Console.WriteLine("Bruteforced {1} combinations in {0} ms", s.ElapsedMilliseconds, total * 999000);
            await progressReporter;
        }
    }
}
