using System;
using System.Collections.Generic;
using System.Linq;

using DieselEngineFormats.ScriptData;

namespace DieselBundleViewer.Services
{
    static class ScriptDataQuery
    {
        public static IEnumerable<T> Concat<T>(params IEnumerable<T>[] sequences)
        {
            foreach (var seq in sequences)
                foreach (var item in seq)
                    yield return item;
        }

        public static IEnumerable<T> Entry<T>(this Dictionary<string,object> self, string key)
        {
            if (self.TryGetValue(key, out object ovalue))
                if (ovalue is T value)
                    return Enumerable.Repeat(value, 1);
            return Enumerable.Empty<T>();
        }

        public static IEnumerable<Dictionary<string, object>> EntryTable(this Dictionary<string, object> self, string key)
            => self.Entry<Dictionary<string, object>>(key);

        public static IEnumerable<T> Entry<T>(this IEnumerable<Dictionary<string, object>> self, string key)
        {
            foreach (var dict in self)
            {
                if (dict.TryGetValue(key, out object ovalue))
                    if (ovalue is T value)
                        yield return value;
            }
        }

        public static IEnumerable<Dictionary<string, object>> EntryTable(this IEnumerable<Dictionary<string, object>> self, string key)
            => self.Entry<Dictionary<string, object>>(key);

        public static IEnumerable<T> Children<T>(this IEnumerable<Dictionary<string, object>> self)
            => self.SelectMany(i => i.Values).OfType<T>();

        public static IEnumerable<Dictionary<string, object>> TableChildren(this IEnumerable<Dictionary<string, object>> self)
            => self.Children<Dictionary<string, object>>();
        public static IEnumerable<Dictionary<string, object>> TableChildren(this Dictionary<string, object> self)
            => self.Values.OfType<Dictionary<string, object>>();

        public static IEnumerable<Dictionary<string, object>> WhereMeta(this IEnumerable<Dictionary<string, object>> self, string meta)
            => self.Where(i => i.ContainsKey("_meta") && (i["_meta"] as string) == meta);

        public static IEnumerable<Dictionary<string, object>> WhereMeta(this IEnumerable<object> self, string meta)
            => self.OfType< Dictionary<string, object>>().Where(i => i.ContainsKey("_meta") && (i["_meta"] as string) == meta);
    }
}
