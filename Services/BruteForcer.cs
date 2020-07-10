using DieselBundleViewer.Models;
using DieselBundleViewer.ViewModels;
using DieselEngineFormats.Utils;
using System;
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
    static class StreamNameBruteForcer
    {
        internal static async Task Run(Dictionary<uint, FileEntry> fileEntries, string outPath, IProgress<ProgressRecord> progress, CancellationToken ct)
        {
            var s = new System.Diagnostics.Stopwatch();
            s.Start();
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
                for(var i = 0; i < interestingBanks.Count; i++)
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
    }
}
