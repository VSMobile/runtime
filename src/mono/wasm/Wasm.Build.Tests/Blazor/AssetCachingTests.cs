// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;

#nullable enable

namespace Wasm.Build.Tests.Blazor;

public class AssetCachingTests : BlazorWasmTestBase
{
    public AssetCachingTests(ITestOutputHelper output, SharedBuildPerTestClassFixture buildContext)
        : base(output, buildContext)
    {
    }

    [Fact, TestCategory("no-fingerprinting")]
    [ActiveIssue("https://github.com/dotnet/runtime/issues/122338")] // add it back to eng\testing\scenarios\BuildWasmAppsJobsList.txt
    public async Task BlazorApp_BasedOnFingerprinting_LoadsWasmAssetsFromCache()
    {
        var project = CopyTestAsset(
            Configuration.Release,
            aot: false,
            TestAsset.BlazorWebWasm,
            "AssetCachingTest",
            appendUnicodeToPath: false
        );

        (string projectDir, string output) = BlazorPublish(project, Configuration.Release, new PublishOptions(AssertAppBundle: false));

        var counterLoaded = new TaskCompletionSource();
        var wasmRequestRecorder = new WasmRequestRecorder();

        var runOptions = new BlazorRunOptions(Configuration.Release)
        {
            BrowserPath = "/counter",
            OnServerMessage = wasmRequestRecorder.RecordIfWasmRequestFinished,
            OnConsoleMessage = (type, msg) =>
            {
                if (msg.Contains("Counter.OnAfterRender"))
                    counterLoaded.SetResult();
            },
            Test = async (page) =>
            {
                await counterLoaded.Task;
                var responseCodes = wasmRequestRecorder.GetResponseCodes();

                // Check server request logs after the first load.
                Assert.NotEmpty(responseCodes);
                Assert.All(responseCodes, r => Assert.Equal(200, r.ResponseCode));

                if (responseCodes.Count != wasmRequestRecorder.GetResponseCodes().Count)
                {
                    Console.WriteLine ($"warning");
                }

                wasmRequestRecorder = new();
                counterLoaded = new();

                // Perform browser navigation to cause resource reload.
                await page.ReloadAsync();
                await counterLoaded.Task;
                responseCodes = wasmRequestRecorder.GetResponseCodes();

                // Check server logs after the second load.
                if (EnvironmentVariables.UseFingerprinting)
                {
                    // With fingerprinting we should not see any requests for Wasm assets during the second load.
                    Assert.Empty(wasmRequestRecorder.ResponseCodes);
                }
                else
                {
                    // Without fingerprinting we should see validation requests for Wasm assets
                    // during the second load with response status 304.
                    Assert.NotEmpty(wasmRequestRecorder.ResponseCodes);
                    Assert.All(wasmRequestRecorder.ResponseCodes, r => Assert.Equal(304, r.ResponseCode));
                }

                await page.EvaluateAsync("console.log('WASM EXIT 0');");
            }
        };

        var buildPaths = GetBuildPaths(Configuration.Release, forPublish: true, projectDir: projectDir);
        var publishedAppPath = Path.Combine(buildPaths.BinDir, "publish");
        var publishedAppDllFileName = $"{project.ProjectName}.dll";
        using ToolCommand cmd = new DotNetCommand(s_buildEnv, _testOutput).WithWorkingDirectory(publishedAppPath);
        var result = await BrowserRun(cmd, $"exec {publishedAppDllFileName}", runOptions);
    }
}

partial class WasmRequestRecorder
{
    private readonly object responseCodesLock = new();
    private readonly List<(string Name, int ResponseCode)> responseCodes = new();

    public List<(string Name, int ResponseCode)> GetResponseCodes()
    {
        lock (responseCodesLock)
        {
            return new List<(string Name, int ResponseCode)>(responseCodes);
        }
    }

    [GeneratedRegex(@"Request finished HTTP/\d\.\d GET http://[^/]+/(?<name>[^\s]+\.wasm)\s+-\s+(?<code>\d+)")]
    private static partial Regex LogRegex();

    public void RecordIfWasmRequestFinished(string message)
    {
        var match = LogRegex().Match(message);

        if (match.Success)
        {
            var name = match.Groups["name"].Value;
            var code = int.Parse(match.Groups["code"].Value);
            lock (responseCodesLock)
            {
                responseCodes.Add((name, code));
            }
        }
    }
}
