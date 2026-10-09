/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using System.Runtime.CompilerServices;

namespace WebCore.Deploy.Tests;

/// <summary>A throwaway app repository with a deploy/deploy.json, which the tool is pointed at before anything reads its settings.</summary>
internal static class TestRepo
{
    public const string Settings = """
        {
          "app": "Sample",
          "webProject": "src/Sample.Web",
          "targets": {
            "stage": { "app": "sample-stage", "group": "rg-sample", "url": "https://stage.example.com", "keyEnv": "SAMPLE_KEY" },
            "prod": { "app": "sample", "group": "rg-sample", "url": "https://www.example.com", "keyEnv": "SAMPLE_KEY", "workspace": "ws" }
          },
          "consoleActions": [
            { "id": "{target}-flag-on", "group": "Flags", "label": "Turn the flag on for {target}", "args": [ "setting", "{target}", "Feature__Flag=On" ], "confirm": "Turn the flag on for {live}?", "targets": [ "stage", "prod" ] },
            { "id": "flag-read", "group": "Flags", "label": "Show the flag", "args": [ "setting", "prod", "Feature__Flag" ] }
          ]
        }
        """;

    public static string Root { get; } = Create();

    /// <summary>Runs before any test: points the tool at the sample repository.</summary>
    [ModuleInitializer]
    public static void Point() => Environment.SetEnvironmentVariable("WEBCORE_DEPLOY_ROOT", Root);

    private static string Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "webcore-deploy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "deploy"));
        File.WriteAllText(Path.Combine(root, "deploy", "deploy.json"), Settings);
        return root;
    }
}
