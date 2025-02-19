﻿// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.

using System.Diagnostics;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.AWS.Utils.Internal;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.AWS.Utils;

namespace Aspire.Hosting.AWS.Lambda;

/// <summary>
/// Lambda lifecycle hook takes care of getting Amazon.Lambda.TestTool installed if there was
/// a Lambda service emulator added to the resources.
/// </summary>
/// <param name="logger"></param>
internal class LambdaLifecycleHook(ILogger<LambdaEmulatorResource> logger, IProcessCommandService processCommandService) : IDistributedApplicationLifecycleHook
{
    internal const string DefaultLambdaTestToolVersion = "0.0.2-preview";

    public async Task BeforeStartAsync(DistributedApplicationModel appModel, CancellationToken cancellationToken = default)
    {
        SdkUtilities.BackgroundSDKDefaultConfigValidation(logger);

        var classLibraryProjectPaths = 
            appModel.Resources
                .OfType<LambdaProjectResource>()
                .Where(x =>
                {
                    if (!x.TryGetLastAnnotation<LambdaFunctionAnnotation>(out var functionAnnotation))
                        return false;

                    if (!functionAnnotation.Handler.Contains("::"))
                        return false;

                    return true;
                })
                .ToList();
        var wrapperProjectPaths = 
            appModel.Resources
                .OfType<LambdaProjectResource>()
                .Where(x =>
                {
                    if (!x.TryGetLastAnnotation<LambdaProjectMetadata>(out _))
                        return false;

                    return true;
                })
                .ToList();
        
        LambdaEmulatorAnnotation? emulatorAnnotation = null;
        if (appModel.Resources.FirstOrDefault(x => x.TryGetLastAnnotation<LambdaEmulatorAnnotation>(out emulatorAnnotation)) != null && emulatorAnnotation != null)
        {
            await ApplyLambdaEmulatorAnnotationAsync(emulatorAnnotation, cancellationToken);

            foreach (var projectResource in wrapperProjectPaths)
            {
                var projectMetadata = projectResource.Annotations
                    .OfType<IProjectMetadata>()
                    .First();
                
                RunProcess("dotnet", $"build {new FileInfo(projectMetadata.ProjectPath).Name}", Directory.GetParent(projectMetadata.ProjectPath)!.FullName);
            }

            foreach (var projectResource in classLibraryProjectPaths)
            {
                var projectMetadata = projectResource.Annotations
                    .OfType<IProjectMetadata>()
                    .First();
                var lambdaFunctionAnnotation = projectResource.Annotations
                    .OfType<LambdaFunctionAnnotation>()
                    .First();
            
                var installPath = await GetCurrentInstallPathAsync(cancellationToken);
                if (string.IsNullOrEmpty(installPath))
                {
                    logger.LogError("Failed to determine The location of Amazon.Lambda.TestTool on disk which is required for running class library Lambda functions.");
                    return;
                }
                var contentFolder = new DirectoryInfo(installPath).Parent?.Parent?.Parent?.FullName;
                if (string.IsNullOrEmpty(contentFolder))
                {
                    logger.LogError("Failed to determine the content folder of Amazon.Lambda.TestTool NuGet package which is required for running class library Lambda functions.");
                    return;
                }
                var targetFramework = await GetProjectTargetFrameworkAsync(projectMetadata.ProjectPath, cancellationToken);
                if (string.IsNullOrEmpty(targetFramework))
                {
                    logger.LogError("Cannot determine the target framework of the project '{ProjectPath}'", projectMetadata.ProjectPath);
                    continue;
                }
                var assemblyName = await GetProjectAssemblyNameAsync(projectMetadata.ProjectPath, cancellationToken);
                if (string.IsNullOrEmpty(assemblyName))
                {
                    logger.LogError("Cannot determine the assembly name of the project '{ProjectPath}'", projectMetadata.ProjectPath);
                    continue;
                }
                var runtimeSupportAssemblyPath = Path.Combine(contentFolder, "content", "Amazon.Lambda.RuntimeSupport",
                    targetFramework, "Amazon.Lambda.RuntimeSupport.dll");
                if (!File.Exists(runtimeSupportAssemblyPath))
                {
                    logger.LogError("Cannot find a version of Amazon.Lambda.RuntimeSupport that supports your project's target framework '{Framework}'. The following directory does not exist '{RuntimeSupportPath}'.", targetFramework, runtimeSupportAssemblyPath);
                    continue;
                }
                ProjectUtilities.UpdateLaunchSettingsWithLambdaTester(
                    resourceName: projectResource.Name,
                    functionHandler: lambdaFunctionAnnotation.Handler,
                    assemblyName: assemblyName,
                    projectPath: projectMetadata.ProjectPath,
                    runtimeSupportAssemblyPath: runtimeSupportAssemblyPath,
                    targetFramework: targetFramework,
                    logger: logger);
            }
        }
        else
        {
            logger.LogDebug("Skipping installing Amazon.Lambda.TestTool since no Lambda emulator resource was found");
        }
    }
    
    /// <summary>
    /// Utility method for running a command on the commandline. It returns backs the exit code and anything written to stdout or stderr.
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="path"></param>
    /// <param name="arguments"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static int RunProcess(string path, string arguments, string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                WorkingDirectory = workingDirectory,
                FileName = path,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };

        var output = new StringBuilder();

        process.OutputDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                output.Append(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (e.Data != null)
            {
                output.Append(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        process.WaitForExit(int.MaxValue);

        Console.WriteLine(output.ToString());
        
        return process.ExitCode;
    }

    internal async Task ApplyLambdaEmulatorAnnotationAsync(LambdaEmulatorAnnotation emulatorAnnotation, CancellationToken cancellationToken = default)
    {
        if (emulatorAnnotation.DisableAutoInstall)
        {
            return;
        }

        var expectedVersion = emulatorAnnotation.OverrideMinimumInstallVersion ?? DefaultLambdaTestToolVersion;
        var installedVersion = await GetCurrentInstalledVersionAsync(cancellationToken);

        if (ShouldInstall(installedVersion, expectedVersion, emulatorAnnotation.AllowDowngrade))
        {
            logger.LogDebug("Installing .NET Tool Amazon.Lambda.TestTool ({version})", installedVersion);

            var commandLineArgument = $"tool install -g Amazon.Lambda.TestTool --version {expectedVersion}";
            if (emulatorAnnotation.AllowDowngrade)
            {
                commandLineArgument += " --allow-downgrade";
            }

            var result = await processCommandService.RunProcessAndCaptureOuputAsync(logger, "dotnet", commandLineArgument, cancellationToken);
            if (result.ExitCode == 0)
            {
                if (!string.IsNullOrEmpty(installedVersion))
                {
                    logger.LogInformation("Successfully Updated .NET Tool Amazon.Lambda.TestTool from version {installedVersion} to {newVersion}", installedVersion, expectedVersion);
                }
                else
                {
                    logger.LogInformation("Successfully installed .NET Tool Amazon.Lambda.TestTool ({version})", expectedVersion);
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(installedVersion))
                {
                    logger.LogWarning("Failed to update Amazon.Lambda.TestTool from {installedVersion} to {expectedVersion}:\n{output}", installedVersion, expectedVersion, result.Output);
                }
                else
                {
                    logger.LogError("Fail to install Amazon.Lambda.TestTool ({version}) required for running Lambda functions locally:\n{output}", expectedVersion, result.Output);
                }
            }
        }
        else
        {
            logger.LogInformation("Amazon.Lambda.TestTool version {version} already installed", installedVersion);
        }
    }

    internal static bool ShouldInstall(string currentInstalledVersion, string expectedVersionStr, bool allowDowngrading)
    {
        if (string.IsNullOrEmpty(currentInstalledVersion))
        {
            return true;
        }

        var installedVersion = Version.Parse(currentInstalledVersion.Replace("-preview", string.Empty));
        var expectedVersion = Version.Parse(expectedVersionStr.Replace("-preview", string.Empty));

        return (installedVersion < expectedVersion) || (allowDowngrading && installedVersion != expectedVersion);
    }

    private async Task<string> GetCurrentInstalledVersionAsync(CancellationToken cancellationToken)
    {
        var results = await processCommandService.RunProcessAndCaptureOuputAsync(logger, "dotnet", "lambda-test-tool info --format json", cancellationToken);
        if (results.ExitCode != 0)
        {
            return string.Empty;
        }

        try
        {
            var versionDoc = JsonNode.Parse(results.Output);
            if (versionDoc == null)
            {
                logger.LogWarning("Error parsing version information from Amazon.Lambda.TestTool: {versionInfo}", results.Output);
                return string.Empty;

            }
            var version = versionDoc["Version"]?.ToString();
            logger.LogDebug("Installed version of Amazon.Lambda.TestTool is {version}", version);
            return version ?? string.Empty;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Error parsing version information from Amazon.Lambda.TestTool: {versionInfo}", results.Output);
            return string.Empty;
        }
    }

    internal async Task<string> GetCurrentInstallPathAsync(CancellationToken cancellationToken)
    {
        var results = await processCommandService.RunProcessAndCaptureOuputAsync(logger, "dotnet", "lambda-test-tool info --format json", cancellationToken);
        if (results.ExitCode != 0)
        {
            return string.Empty;
        }

        try
        {
            var installPathDoc = JsonNode.Parse(results.Output);
            if (installPathDoc == null)
            {
                logger.LogWarning("Error parsing install path information from Amazon.Lambda.TestTool: {versionInfo}", results.Output);
                return string.Empty;

            }
            var installPath = installPathDoc["InstallPath"]?.ToString();
            logger.LogDebug("Install path of Amazon.Lambda.TestTool is {version}", installPath);
            return installPath ?? string.Empty;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Error parsing install path information from Amazon.Lambda.TestTool: {versionInfo}", results.Output);
            return string.Empty;
        }
    }

    internal async Task<string> GetProjectAssemblyNameAsync(string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            var results = await processCommandService.RunProcessAndCaptureOuputAsync(logger, "dotnet", $"msbuild \"{projectPath}\" -nologo -v:q -getProperty:AssemblyName", cancellationToken);
            if (results.ExitCode != 0)
            {
                return string.Empty;
            }

            logger.LogDebug("The assembly name of '{projectPath}' is {assemblyName}", projectPath, results.Output);
            return results.Output;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Error retrieving the assembly name of '{projectPath}'", projectPath);
            return string.Empty;
        }
    }

    internal async Task<string> GetProjectTargetFrameworkAsync(string projectPath, CancellationToken cancellationToken)
    {
        try
        {
            var results = await processCommandService.RunProcessAndCaptureOuputAsync(logger, "dotnet", $"msbuild \"{projectPath}\" -nologo -v:q -getProperty:TargetFramework", cancellationToken);
            if (results.ExitCode != 0)
            {
                return string.Empty;
            }

            logger.LogDebug("The target framework of '{projectPath}' is {targetFramework}", projectPath, results.Output);
            return results.Output;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Error retrieving the target framework of '{projectPath}'", projectPath);
            return string.Empty;
        }
    }
}
