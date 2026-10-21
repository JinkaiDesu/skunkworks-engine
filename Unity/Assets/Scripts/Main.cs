using System;
using System.Threading;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[DefaultExecutionOrder(32000)]
public sealed class Main : MonoBehaviour
{
    private static Main owner;
    private ReflectionBroker reflection;
    private ControlServer control;
    private RuntimeBoundary boundary;
    private Skunkworks.System.WindowControl window;
    private InputEvents input;
    private int stopped = 1;
    private string reportedInputError;

    private void Awake()
    {
        if (owner != null && owner != this) { Destroy(gameObject); return; }
        owner = this;
        // The recovery substrate must outlive authored scenes and scene-bundle swaps.
        DontDestroyOnLoad(gameObject);
        Interlocked.Exchange(ref stopped, 0);
        Application.runInBackground = true;
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
#endif
        SystemPaths.Initialize();
        reflection = new ReflectionBroker();
        reflection.RegisterRoot("reflection", reflection);
        reflection.RegisterRoot("values", new NativeValues(reflection.DecodeValue));
        boundary = new RuntimeBoundary();
        reflection.RegisterRoot("boundary", boundary);
        window = new Skunkworks.System.WindowControl(!Application.isEditor);
        reflection.RegisterRoot("window", window);
        try
        {
            input = GetComponent<InputEvents>() ?? gameObject.AddComponent<InputEvents>();
            input.StartCapture();
            reflection.RegisterRoot("input", input.Program);
            SystemPaths.Log("skunkworks", input.Program.Describe());
        }
        catch (Exception error) { Report("input", error); }
        try
        {
            control = new ControlServer(SystemPaths.UnityPipe);
            control.Start();
            reflection.RegisterRoot("dispatch", control.Status);
            SystemPaths.Log("skunkworks", "IPC ready");
        }
        catch (Exception error) { Report("IPC", error); }
        try { reflection.RegisterRoot("terminal", Skunkworks.System.TerminalHost.Controller); Skunkworks.System.TerminalHost.Start(); }
        catch (Exception error) { Report("terminal", error); }
    }

    private void Update()
    {
        if (Volatile.Read(ref stopped) != 0) return;
        window?.Pump();
        input?.Program?.Pump();
        string error = input?.Program?.LastSubscriberError;
        if (!string.IsNullOrEmpty(error) && error != reportedInputError)
        {
            reportedInputError = error;
            SystemPaths.Log("input", error);
            Debug.LogError("[input] " + error);
        }
        control?.Pump(reflection.HandleOwned, reflection.ReleaseOwner, 64, reflection.DescribeRequest);
    }

    private static void Report(string source, Exception error)
    {
        SystemPaths.Log("skunkworks", source + " failed: " + error);
        Debug.LogException(error);
    }
    private void FixedUpdate() { if (Volatile.Read(ref stopped) == 0) boundary?.Pump(); }
    private void OnDisable() => Stop();
    private void OnDestroy() => Stop();
    private void OnApplicationQuit() => Stop();
    private void Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        if (owner == this) owner = null;
#if UNITY_EDITOR
        AssemblyReloadEvents.beforeAssemblyReload -= Stop;
#endif
        try { Skunkworks.System.TerminalHost.Stop(); } catch (Exception error) { Report("terminal stop", error); }
        try { input?.StopCapture(); } catch (Exception error) { Report("input stop", error); }
        try { control?.Dispose(); } catch (Exception error) { Report("IPC stop", error); }
        window?.Dispose(); window = null;
        boundary?.Dispose(); boundary = null;
        control = null; input = null; reflection = null;
        SystemPaths.Log("skunkworks", "stopped");
    }
}
