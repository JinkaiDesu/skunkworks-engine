using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace Skunkworks.System
{
    // Distribution policy is data. The renderer can supervise any console program.
    internal sealed class ShellLaunch
    {
        public string Executable, Arguments = "", StartupVariable = "", StatusVariable = "";
        public string StartupTemplate = "", StartupFile = "";
        private readonly Dictionary<string, string> environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> paths = new List<string>();

        public static ShellLaunch Load(string path, string root, string user)
        {
            string Expand(string text) => Environment.ExpandEnvironmentVariables((text ?? "").Replace("{root}", root).Replace("{user}", user));
            var document = XDocument.Load(path);
            var node = document.Root;
            if (node == null || node.Name != "terminal") throw new InvalidDataException("Expected a terminal configuration.");
            var launch = new ShellLaunch { Executable = Expand((string)node.Attribute("executable")), Arguments = Expand((string)node.Attribute("arguments")) };
            if (string.IsNullOrWhiteSpace(launch.Executable)) throw new InvalidDataException("Terminal executable is required.");
            if (!Path.IsPathRooted(launch.Executable)) launch.Executable = Path.GetFullPath(Path.Combine(root, launch.Executable));
            foreach (var entry in node.Elements("environment"))
            {
                string name = (string)entry.Attribute("name");
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '=', '\0' }) >= 0) throw new InvalidDataException("Invalid environment name.");
                launch.environment.Add(name, Expand((string)entry.Attribute("value")));
            }
            foreach (var entry in node.Elements("path")) launch.paths.Add(Expand((string)entry.Attribute("value")));
            var startup = node.Element("startup");
            if (startup != null)
            {
                launch.StartupVariable = (string)startup.Attribute("variable") ?? "";
                launch.StatusVariable = (string)startup.Attribute("statusVariable") ?? "";
                launch.StartupTemplate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path), Expand((string)startup.Attribute("template"))));
                launch.StartupFile = Expand((string)startup.Attribute("file"));
                if (launch.StartupVariable.Length == 0 || !Path.IsPathRooted(launch.StartupFile)) throw new InvalidDataException("Startup needs a variable and absolute file.");
            }
            return launch;
        }

        public void Apply(IDictionary target)
        {
            foreach (var entry in environment) if (!target.Contains(entry.Key)) target[entry.Key] = entry.Value;
            if (paths.Count != 0) target["PATH"] = string.Join(";", paths) + ";" + target["PATH"];
            if (StartupVariable.Length != 0) target[StartupVariable] = StartupFile;
        }
    }
}
