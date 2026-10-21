#if UNITY_EDITOR

using System;
using System.Collections.Generic;
using System.IO;

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

public static class Build
{
    public static void Windows()
    {
        // Optional process-local setting for build agents with limited memory.
        string workerSetting = Environment.GetEnvironmentVariable("SKUNK_BUILD_IMPORT_WORKERS");
        if (!string.IsNullOrEmpty(workerSetting))
        {
            if (!int.TryParse(workerSetting, out int workers) || workers < 1)
                throw new ArgumentException("SKUNK_BUILD_IMPORT_WORKERS must be a positive integer.");
            AssetDatabase.DesiredWorkerCount = workers;
            AssetDatabase.ForceToDesiredWorkerCount();
        }

        string output =
            Environment.GetEnvironmentVariable(
                "UNITY_BUILD_OUTPUT"
            );


        if (
            string.IsNullOrWhiteSpace(
                output
            )
        )
        {
            throw new InvalidOperationException(
                "UNITY_BUILD_OUTPUT is not set."
            );
        }


        output =
            Path.GetFullPath(
                output
            );


        Directory.CreateDirectory(
            output
        );


        Console.WriteLine(
            "UNITY_BUILD_OUTPUT=" +
            output
        );


        // ----------------------------------------------------
        // WINDOWS RELEASE CONTRACT
        // ----------------------------------------------------

        PlayerSettings.productName = "Skunkworks";

        NamedBuildTarget target =
            NamedBuildTarget.Standalone;


        PlayerSettings.SetScriptingBackend(
            target,
            ScriptingImplementation.Mono2x
        );


        PlayerSettings.SetManagedStrippingLevel(
            target,
            ManagedStrippingLevel.Disabled
        );


        // ----------------------------------------------------
        // EXACTLY ONE ENABLED SCENE
        // ----------------------------------------------------

        var scenes =
            new List<string>();


        EditorBuildSettingsScene[] configured =
            EditorBuildSettings.scenes;


        for (
            int i = 0;
            i < configured.Length;
            i++
        )
        {
            EditorBuildSettingsScene scene =
                configured[i];


            if (
                scene != null &&
                scene.enabled
            )
            {
                scenes.Add(
                    scene.path
                );
            }
        }


        Console.WriteLine(
            "Enabled scenes: " +
            scenes.Count
        );


        for (
            int i = 0;
            i < scenes.Count;
            i++
        )
        {
            Console.WriteLine(
                "Scene[" +
                i +
                "]=" +
                scenes[i]
            );
        }


        if (
            scenes.Count !=
            1
        )
        {
            throw new InvalidOperationException(
                "Standalone build requires exactly one enabled scene. " +
                "Found " +
                scenes.Count +
                "."
            );
        }


        string executable =
            Path.Combine(
                output,
                "Skunkworks.exe"
            );


        Console.WriteLine(
            "Building player:"
        );

        Console.WriteLine(
            executable
        );


        var options =
            new BuildPlayerOptions
            {
                scenes =
                    scenes.ToArray(),

                locationPathName =
                    executable,

                target =
                    BuildTarget.StandaloneWindows64,

                options =
                    BuildOptions.None
            };


        BuildReport report =
            BuildPipeline.BuildPlayer(
                options
            );


        BuildSummary summary =
            report.summary;


        Console.WriteLine(
            "Build result: " +
            summary.result
        );


        Console.WriteLine(
            "Build output: " +
            summary.outputPath
        );


        Console.WriteLine(
            "Build size: " +
            summary.totalSize
        );


        Console.WriteLine(
            "Errors: " +
            summary.totalErrors
        );


        Console.WriteLine(
            "Warnings: " +
            summary.totalWarnings
        );


        if (
            summary.result !=
            BuildResult.Succeeded
        )
        {
            throw new Exception(
                "Unity player build failed: " +
                summary.result
            );
        }


        if (
            !File.Exists(
                executable
            )
        )
        {
            throw new FileNotFoundException(
                "Unity reported success but Skunkworks.exe does not exist.",
                executable
            );
        }


        Console.WriteLine(
            "UNITY BUILD OK"
        );
    }
}

#endif
