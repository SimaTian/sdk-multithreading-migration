// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace UnsafeThreadSafeTasks.IntermittentViolations
{
    /// <summary>
    /// Caches resolved configuration file paths across task invocations using
    /// IBuildEngine4 registered task objects. The first invocation resolves ConfigFileName
    /// with Path.GetFullPath (relative to the process CWD) and stores the result.
    /// Subsequent invocations reuse the cached path — which is correct only when every
    /// invocation shares the same CWD. In multi-threaded builds, projects in different
    /// directories share the cache but have different intended base directories.
    /// </summary>
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
            string cacheKey = BuildCacheKey(ConfigFileName);

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

        /// <summary>
        /// Builds a cache key that is shared across all task invocations regardless of project.
        /// VIOLATION: The key does not incorporate the project directory, so every project that
        /// uses the same ConfigFileName gets the same cached (and potentially wrong) path.
        /// Fix: include TaskEnvironment.ProjectDirectory in the key.
        /// </summary>
        private static string BuildCacheKey(string configFileName)
        {
            return $"{CacheKeyPrefix}_{configFileName}";
        }

        /// <summary>
        /// Resolves the config file name to an absolute path and stores it in the shared state.
        /// VIOLATION: Uses Path.GetFullPath, which resolves relative to Environment.CurrentDirectory.
        /// In multi-threaded builds the CWD may belong to another project's thread.
        /// Fix: use TaskEnvironment.GetAbsolutePath(configFileName).
        /// </summary>
        private SharedConfigState InitializeState(string configFileName)
        {
            var state = new SharedConfigState();

            string resolvedPath = Path.GetFullPath(configFileName);
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

        /// <summary>
        /// Shared state cached via IBuildEngine4.RegisterTaskObject. Because the cache key
        /// does not include the project directory, all projects share a single instance whose
        /// ResolvedPaths were computed relative to whichever CWD was active first.
        /// </summary>
        internal sealed class SharedConfigState
        {
            public Dictionary<string, string> ResolvedPaths { get; } =
                new(StringComparer.OrdinalIgnoreCase);

            public bool IsInitialized { get; set; }

            public string ConfigContent { get; set; } = string.Empty;
        }
    }
}
