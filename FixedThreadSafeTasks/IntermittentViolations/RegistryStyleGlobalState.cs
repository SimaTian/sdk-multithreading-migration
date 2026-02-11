// FIXED: Includes ProjectDirectory in cache key, uses TaskEnvironment.GetAbsolutePath().
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace FixedThreadSafeTasks.IntermittentViolations
{
    [MSBuildMultiThreadableTask]
    public class RegistryStyleGlobalState : Microsoft.Build.Utilities.Task, IMultiThreadableTask
    {
        public TaskEnvironment TaskEnvironment { get; set; } = new();

        private const string CacheKeyPrefix = "RegistryStyleGlobalState_ConfigCache";

        [Required]
        public string ConfigFileName { get; set; } = string.Empty;

        [Output]
        public string ConfigFilePath { get; set; } = string.Empty;

        [Output]
        public bool ConfigLoaded { get; set; }

        public override bool Execute()
        {
            if (string.IsNullOrWhiteSpace(ConfigFileName))
            {
                Log.LogError("ConfigFileName must be specified.");
                return false;
            }

            IBuildEngine4 engine4 = (IBuildEngine4)BuildEngine;
            // FIX: Include ProjectDirectory in cache key
            string cacheKey = BuildCacheKey(ConfigFileName, TaskEnvironment.ProjectDirectory);

            SharedConfigState? state = engine4.GetRegisteredTaskObject(
                cacheKey, RegisteredTaskObjectLifetime.Build) as SharedConfigState;

            if (state != null && state.IsInitialized)
            {
                Log.LogMessage(MessageImportance.Low,
                    "Cache hit for '{0}' — reusing resolved path '{1}'.",
                    ConfigFileName, state.ResolvedPaths[ConfigFileName]);

                ConfigFilePath = state.ResolvedPaths[ConfigFileName];
                ConfigLoaded = ValidateCachedData(state);
                return true;
            }

            state = InitializeState(ConfigFileName);

            engine4.RegisterTaskObject(
                cacheKey, state, RegisteredTaskObjectLifetime.Build, allowEarlyCollection: false);

            ConfigFilePath = state.ResolvedPaths[ConfigFileName];
            ConfigLoaded = state.IsInitialized;

            Log.LogMessage(MessageImportance.Normal,
                "Resolved configuration file '{0}' -> '{1}'.", ConfigFileName, ConfigFilePath);

            return true;
        }

        // FIX: Include projectDirectory in key
        private static string BuildCacheKey(string configFileName, string projectDirectory)
        {
            return $"{CacheKeyPrefix}_{projectDirectory}_{configFileName}";
        }

        private SharedConfigState InitializeState(string configFileName)
        {
            var state = new SharedConfigState();

            // FIX: Use TaskEnvironment.GetAbsolutePath instead of Path.GetFullPath
            string resolvedPath = TaskEnvironment.GetAbsolutePath(configFileName);
            state.ResolvedPaths[configFileName] = resolvedPath;

            if (File.Exists(resolvedPath))
            {
                state.IsInitialized = true;
                state.ConfigContent = File.ReadAllText(resolvedPath);
                Log.LogMessage(MessageImportance.Low,
                    "Loaded config content ({0} chars) from '{1}'.",
                    state.ConfigContent.Length, resolvedPath);
            }
            else
            {
                state.IsInitialized = false;
                Log.LogWarning("Configuration file '{0}' not found at resolved path '{1}'.",
                    configFileName, resolvedPath);
            }

            return state;
        }

        private bool ValidateCachedData(SharedConfigState state)
        {
            if (!state.IsInitialized)
                return false;

            string cachedPath = state.ResolvedPaths[ConfigFileName];
            if (!File.Exists(cachedPath))
            {
                Log.LogWarning(
                    "Cached config path '{0}' no longer exists on disk; cache may be stale.",
                    cachedPath);
                return false;
            }

            return true;
        }

        internal sealed class SharedConfigState
        {
            public Dictionary<string, string> ResolvedPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public bool IsInitialized { get; set; }

            public string ConfigContent { get; set; } = string.Empty;
        }
    }
}
