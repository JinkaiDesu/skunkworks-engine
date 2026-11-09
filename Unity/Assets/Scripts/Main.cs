using System;
using UnityEngine;

// The Skunkworks host: a blank Unity canvas. Unity owns the window, the renderer and the player loop;
// this object owns startup, shutdown and saying what happened in the player log.
//
//   Foreign input   InputEvents copies device facts into InputProgram, a bounded journal read by sequence.
//   Foreign output  Unity's renderer. The scene's camera clears the canvas; nothing presents on it yet.
//
// The computation this hosts is external and its transport is not designed, so nothing here reaches for it.
// There is no Update and no FixedUpdate: Unity's frame and physics steps pace Unity's own work, nothing else.
public sealed class Main : MonoBehaviour
{
    private InputEvents input;

    private void Awake()
    {
        try
        {
            input = gameObject.AddComponent<InputEvents>();
            input.StartCapture();
            Debug.Log("[skunkworks] started: " + input.Program.Describe());
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            Debug.LogError("[skunkworks] started without input: " + error.Message);
        }
    }

    private void OnApplicationQuit()
    {
        string state = "no input";
        try
        {
            if (input != null && input.Program != null)
            {
                state = input.Program.Describe();
                input.StopCapture();
            }
        }
        catch (Exception error) { Debug.LogException(error); }
        Debug.Log("[skunkworks] stopped: " + state);
    }
}
