using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DesktopBar.Services
{
    internal static class MiniFolderCatalog
    {
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
