using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>Installation-relative system paths. No packaged developer fallback.</summary>
public static class SystemPaths
{
    public static string Root { get; private set; }
    public static string System => Path.Combine(Root, "System");
    public static string Toolchain => Path.Combine(Root, "Toolchain");
    public static string Database => Path.Combine(Root, "Database");
    public static string Worlds => Path.Combine(UserRoot, "Worlds");
    public static string InstallRoot => Root;
    public static string SystemRoot => System;
    public static string UserRoot { get; private set; }
    public static string CacheRoot { get; private set; }
    public static string TempRoot => Path.GetTempPath();
    public static string Logs { get; private set; }
    public static string UnityPipe { get; private set; }
    internal static Skunkworks.System.ShellLaunch Shell { get; private set; }
    private static readonly object logGate = new object();

    public static void Initialize()
    {
#if UNITY_EDITOR
        Root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "Release"));
#else
        Root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
#endif
        UnityPipe = "skunkworks.unity." + global::System.Diagnostics.Process.GetCurrentProcess().Id;
        string selected = Environment.GetEnvironmentVariable("SKUNK_USER");
        if (!string.IsNullOrEmpty(selected) && !Path.IsPathRooted(selected))
            throw new ArgumentException("SKUNK_USER must be an absolute writable directory.");
        UserRoot = string.IsNullOrEmpty(selected) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skunkworks") : selected;
        if (string.IsNullOrEmpty(selected) && string.IsNullOrEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            UserRoot = Application.persistentDataPath;
        UserRoot = Path.GetFullPath(UserRoot);
        CacheRoot = Path.Combine(UserRoot, "Cache");
        Logs = Path.Combine(UserRoot, "Logs");
        foreach (string folder in new[] {UserRoot, CacheRoot, Logs, Worlds, Path.Combine(UserRoot, "Commands"), Path.Combine(UserRoot, "Terminal")})
            Directory.CreateDirectory(folder);
        string configuration = Environment.GetEnvironmentVariable("SKUNK_TERMINAL_CONFIG");
        if (string.IsNullOrEmpty(configuration)) configuration = Path.Combine(Application.streamingAssetsPath, "terminal.config");
        if (!File.Exists(configuration)) { Shell = new Skunkworks.System.ShellLaunch { Executable = "" }; Log("terminal", "no terminal configuration: " + configuration); }
        else try { Shell = Skunkworks.System.ShellLaunch.Load(configuration, Root, UserRoot); }
        catch (Exception error) { Shell = new Skunkworks.System.ShellLaunch { Executable = "" }; Log("terminal", "configuration failed: " + error.Message); }
        try { if (Shell.StartupFile.Length != 0) Skunkworks.System.ShellStartup.EnsureScript(Shell.StartupTemplate, Shell.StartupFile); }
        catch (Exception error) { Log("skunkworks", "startup provisioning failed: " + error.Message); }
        Log("skunkworks", "root=" + Root + " pipe=" + UnityPipe);
    }

    public static IDictionary ChildEnvironment()
    {
        // Mono preserves Windows environment key casing; duplicate Path/PATH entries
        // otherwise let a child pick the inherited system path ahead of bundled tools.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string)entry.Value;
        environment["SKUNK_ROOT"] = Root;
        environment["SKUNK_SYSTEM"] = System;
        environment["SKUNK_TOOLCHAIN"] = Toolchain;
        environment["SKUNK_DATABASE"] = Database;
        environment["SKUNK_WORLDS"] = Worlds;
        environment["SKUNK_UNITY_PIPE"] = UnityPipe;
        environment["SKUNK_LOGS"] = Logs;
        environment["SKUNK_USER"] = UserRoot;
        environment["SKUNK_CACHE"] = CacheRoot;
        environment["SKUNK_TEMP"] = TempRoot;
        if (!environment.ContainsKey("SKUNK_DATA")) environment["SKUNK_DATA"] = Path.Combine(UserRoot, "Data");
        string prefix = Path.Combine(Toolchain, "msys64", "usr", "bin") + ";" +
            Path.Combine(Toolchain, "python") + ";" +
            Path.Combine(Toolchain, "git", "cmd") + ";" +
            Path.Combine(Toolchain, "dotnet") + ";" +
            Path.Combine(Toolchain, "cmake", "bin");
        string inherited = environment.TryGetValue("PATH", out string hostPath) ? hostPath : "";
        if (environment.TryGetValue("SKUNK_HOST_PATH", out string policy) && policy == "0")
            inherited = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        foreach (string entry in (prefix + ";" + inherited).Split(';'))
            if (!string.IsNullOrWhiteSpace(entry) && unique.Add(entry)) directories.Add(entry);
        environment["PATH"] = string.Join(";", directories);
        Shell.Apply(environment);
        environment["TERM"] = "xterm-256color";
        return environment;
    }

    public static void Log(string source, string message)
    {
        try
        {
            lock (logGate) File.AppendAllText(Path.Combine(Logs, source + ".log"),
                DateTimeOffset.Now.ToString("o") + " [" + source + "] " + message + Environment.NewLine);
        }
        catch { /* Logs must never take down recovery infrastructure. */ }
    }
}
