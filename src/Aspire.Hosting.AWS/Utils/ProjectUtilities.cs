using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.AWS.Utils;

internal static class ProjectUtilities
{
    /// <summary>
    /// Initializes the project's launch settings if necessary, and
    /// ensures they are referencing the Amazon.Lambda.TestTool's location.
    /// </summary>
    /// <param name="resourceName">Lambda function name to be used part of the launch setting profile</param>
    /// <param name="functionHandler">Lambda function handler</param>
    /// <param name="assemblyName">Assembly name of the Lambda function to retrieve the deps file and runtime config file</param>
    /// <param name="projectPath">Project file path</param>
    /// <param name="runtimeSupportAssemblyPath">Runtime Support dll path</param>
    /// <param name="targetFramework">Lambda function target framework</param>
    /// <param name="logger">A logger instance</param>
    public static void UpdateLaunchSettingsWithLambdaTester(
        string resourceName, 
        string functionHandler, 
        string assemblyName, 
        string projectPath, 
        string runtimeSupportAssemblyPath, 
        string targetFramework,
        ILogger? logger = null)
    {
        try
        {
            // Retrieve the current launch settings JSON from wherever it's stored.
            string launchSettingsJson = GetLaunchSettings(projectPath);

            // Parse the JSON into a mutable JsonNode (root is expected to be an object)
            JsonNode? rootNode = JsonNode.Parse(launchSettingsJson);
            if (rootNode is not JsonObject root)
            {
                // If the parsed JSON isn’t an object, initialize a new one.
                root = new JsonObject();
            }

            // Get or create the "profiles" JSON object
            JsonObject profiles = root["profiles"]?.AsObject() ?? new JsonObject();
            root["profiles"] = profiles;  // Ensure it's added to the root

            var launchSettingsNodeKey = $"{Constants.LaunchSettingsNodePrefix}{resourceName}";

            // Get or create the specific profile for Amazon.Lambda.TestTool
            JsonObject? lambdaTester = profiles[launchSettingsNodeKey]?.AsObject();
            if (lambdaTester == null)
            {
                lambdaTester = new JsonObject
                {
                    ["commandName"] = "Executable",
                    ["executablePath"] = "dotnet"
                };

                profiles[launchSettingsNodeKey] = lambdaTester;
            }

            // Update properties that contain a path that is environment-specific
            lambdaTester["commandLineArgs"] =
                $"exec --depsfile ./{assemblyName}.deps.json --runtimeconfig ./{assemblyName}.runtimeconfig.json {SubstituteHomePath(runtimeSupportAssemblyPath)} {functionHandler}";
            lambdaTester["workingDirectory"] = Path.Combine(".", "bin", "$(Configuration)", targetFramework);

            // Serialize the updated JSON with indentation
            var options = new JsonSerializerOptions { WriteIndented = true };
            string updatedJson = root.ToJsonString(options);

            // Save the updated JSON back to the launch settings file.
            SaveLaunchSettings(projectPath, updatedJson);
        }
        catch (JsonException ex)
        {
            logger?.LogError(ex, "Failed to parse the launchSettings.json file for the project '{ProjectPath}'.", projectPath);
        }
    }
    
    /// <summary>
    /// Check if a path is in the user profile directory and use the environment-specific environment variable.
    /// </summary>
    /// <param name="path">The path to update to use the user profile environment variable</param>
    /// <returns>A path that uses the user profile environment variable</returns>
    private static string SubstituteHomePath(string path)
    {
        var userProfileEnvironmentVariable = "%USERPROFILE%";
        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            userProfileEnvironmentVariable = "$HOME";
        }

        if (path.StartsWith(userProfilePath))
        {
            return path.Replace(userProfilePath, userProfileEnvironmentVariable);
        }

        return path;
    }
    
    /// <summary>
    /// Retrieve a project's launchSettings.json file contents
    /// </summary>
    /// <param name="projectPath">The project file path</param>
    /// <returns>The launchSetting.json content</returns>
    private static string GetLaunchSettings(string projectPath)
    {
        var parentDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(parentDirectory))
            throw new ArgumentException($"The project path '{projectPath}' is invalid. Unable to retrieve the '{Constants.LaunchSettingsFile}' file.");
        var properties = Path.Combine(parentDirectory, "Properties");
        if (!Directory.Exists(properties))
        {
            return "{}";
        }

        var fullPath = Path.Combine(properties, Constants.LaunchSettingsFile);
        if (!File.Exists(fullPath))
            return "{}";

        return File.ReadAllText(fullPath);
    }

    /// <summary>
    /// Write the launchSettings.json content to disk
    /// </summary>
    /// <param name="projectPath">The project file path</param>
    /// <param name="content">The launchSettings.json content</param>
    private static void SaveLaunchSettings(string projectPath, string content)
    {
        var parentDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(parentDirectory))
            throw new ArgumentException($"The project path '{projectPath}' is invalid. Unable to retrieve the '{Constants.LaunchSettingsFile}' file.");
        var properties = Path.Combine(parentDirectory, "Properties");
        if (!Directory.Exists(properties))
        {
            Directory.CreateDirectory(properties);
        }
        var fullPath = Path.Combine(properties, Constants.LaunchSettingsFile);
        File.WriteAllText(fullPath, content);
    }
}