using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DesktopBar.Services
{
    internal static class MiniFolderCatalog
    {
        internal static void RetireWatcher(FileSystemWatcher watcher)
        {
            if (watcher == null) return;
            watcher.Dispose();
            _ = FinishRetirementAsync(watcher);
        }

        private static async Task FinishRetirementAsync(FileSystemWatcher watcher)
        {
            // The directory handle is disposed above. Keep the watcher reachable while .NET 8's
            // overlapped completion uses its weak reference to release the pinned buffer.
            await Task.Delay(1000).ConfigureAwait(false);
            GC.KeepAlive(watcher);
        }

        internal static string GetFolderPath(string root, string name)
        {
            if (!SettingsService.IsValidFolderName(name)) return null;
            try
            {
                string fullRoot = Path.GetFullPath(root);
                string path = Path.GetFullPath(Path.Combine(fullRoot, name));
                return string.Equals(Path.GetRelativePath(fullRoot, path), name, StringComparison.OrdinalIgnoreCase)
                    ? path : null;
            }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
        }

        internal static string[] ReadNames(string root) => Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(SettingsService.IsValidFolderName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        internal static List<string> Reconcile(IEnumerable<string> order, string[] actualNames)
        {
            var result = new List<string>(actualNames.Length);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in actualNames) names.TryAdd(name, name);
            foreach (var name in order)
                if (names.Remove(name, out var current)) result.Add(current);
            foreach (var name in actualNames)
                if (names.Remove(name)) result.Add(name);
            return result;
        }
    }
}
