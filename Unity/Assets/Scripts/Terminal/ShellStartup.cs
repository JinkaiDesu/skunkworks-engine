using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Skunkworks.System
{
    // Optional startup acknowledgement selected by terminal configuration.
    internal sealed class ShellStartup : IDisposable
    {
        private readonly string statusFile;
        public string State { get; private set; }
        public string Diagnostic { get; private set; } = "";
        public IDictionary Environment { get; }

        public ShellStartup(IDictionary source, string directory, string startupVariable = "", string statusVariable = "")
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in source) values[entry.Key.ToString()] = entry.Value?.ToString() ?? "";
            // Never reuse a parent's completion channel, including on shell restart.
            if (statusVariable.Length != 0) values.Remove(statusVariable);
            State = "None";
            if (startupVariable.Length != 0 && statusVariable.Length != 0 && values.TryGetValue(startupVariable, out string script) && !string.IsNullOrEmpty(script))
            {
                string folder = Path.Combine(directory, "Terminal");
                Directory.CreateDirectory(folder);
                statusFile = Path.Combine(folder, "boot-" + Guid.NewGuid().ToString("N") + ".status");
                values[statusVariable] = statusFile;
                State = "Pending";
            }
            Environment = values;
        }

        public bool Poll()
        {
            if (State != "Pending") return false;
            try
            {
                if (!File.Exists(statusFile)) return false;
                string result = File.ReadAllText(statusFile);
                if (result == "ready\n") { State = "Ready"; return true; }
                if (!result.StartsWith("failed\n", StringComparison.Ordinal)) return false;
                State = "Failed"; Diagnostic = result.Substring(7); return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        public static void EnsureScript(string template, string destination)
        {
            // Existing author choices survive application updates and restarts.
            if (File.Exists(destination)) return;
            using (var input = File.OpenRead(template))
            {
                try { using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write)) input.CopyTo(output); }
                catch (IOException) { if (!File.Exists(destination)) throw; }
            }
        }

        public void Dispose()
        {
            if (statusFile == null) return;
            try { File.Delete(statusFile); File.Delete(statusFile + ".tmp"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
