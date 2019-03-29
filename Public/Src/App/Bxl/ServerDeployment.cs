// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BuildXL.App.Tracing;
using BuildXL.Engine;
using BuildXL.Native.IO;
using BuildXL.Utilities;
using BuildXL.Utilities.Tasks;
using static BuildXL.Utilities.FormattableStringEx;

namespace BuildXL
{
    /// <summary>
    /// Represents a cached deployment used for BuildXL server mode
    /// </summary>
    internal sealed class ServerDeployment
    {
        public readonly string DeploymentPath;

        public readonly ServerDeploymentCacheCreated? CacheCreationInformation;

        // Folder name for the deployment cache, a subfolder of the running client app folder
        private const string ServerDeploymentDirectoryCache = "BuildXLServerDeploymentCache";

        /// <summary>
        /// Filename where the hash of the server deployment is stored
        /// </summary>
        public const string ServerDeploymentHashFilename = "ServerCacheDeployment.hash";

        private const string KillBuildXLServerCommandLine = "wmic";
        private const string KillBuildXLServerCommandLineArgs = @"process where ""ExecutablePath like '%{0}%'"" delete";

        private ServerDeployment(string baseDirectory, ServerDeploymentCacheCreated? cacheCreationInformation)
        {
            DeploymentPath = baseDirectory;
            CacheCreationInformation = cacheCreationInformation;
        }

        /// <summary>
        /// If the deployment hash of the client is not the same as the deployment hash of the server cache, creates a new deployment cache. Otherwise, does nothing.
        /// </summary>
        /// <exception cref="IOException">
        /// Throws if the copy fails</exception>
        public static ServerDeployment GetOrCreateServerDeploymentCache(string serverDeploymentRoot, AppDeployment clientApp)
        {
            var deploymentDir = ComputeDeploymentDir(serverDeploymentRoot);

            ServerDeploymentCacheCreated? cacheCreated = null;
            if (!Directory.Exists(deploymentDir) || clientApp.TimestampBasedHash.ToHex() != GetDeploymentCacheHash(deploymentDir))
            {
                cacheCreated = CreateServerDeployment(deploymentDir, clientApp);
            }

            return new ServerDeployment(deploymentDir, cacheCreated);
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "StreamReader/StreamWriter takes ownership for disposal.")]
        private static ServerDeploymentCacheCreated CreateServerDeployment(string destDir, AppDeployment clientApp)
        {
            Stopwatch st = Stopwatch.StartNew();

            // Every time the server cache gets created, a file with the result of GetDeploymentHash is created, so we avoid computing this again
            // The assumption is that nobody but this process modifies the server cache folder
            string serverDeploymentHashFile = Path.Combine(destDir, ServerDeploymentHashFilename);

            // Deletes the existing cache directory if it exists, so we avoid accumulating garbage.
            if (Directory.Exists(destDir))
            {
                // Check if the main root process (likely bxl.exe) is in use before attempting to delete, so we avoid partially deleting files
                // Not completely bullet proof (there can be a race) but it is highly unlikely the process starts to be in use right after this check
                KillServer(destDir);

                // Delete the deployment hash file first to make sure the deployment cache cannot be used in case the
                // cleanup of the other files is interrupted.
                PoisonServerDeployment(destDir);

                // Remove all files regardless of files being readonly
                FileUtilities.DeleteDirectoryContents(destDir, true);
            }

            // Perform the deployment
            AppDeployment serverDeployment = AppDeployment.ReadDeploymentManifest(clientApp.BaseDirectory, AppDeployment.ServerDeploymentManifestFileName);
            HashSet<string> directories = new HashSet<string>();
            List<KeyValuePair<string, string>> filesToCopy = new List<KeyValuePair<string, string>>();

            foreach (string path in serverDeployment.GetRelevantRelativePaths(forServerDeployment: true).Concat(new string[] { AppDeployment.ServerDeploymentManifestFileName }))
            {
                string targetPath = Path.Combine(destDir, path);
                string sourcePath = Path.Combine(clientApp.BaseDirectory, path);
                string directory = Path.GetDirectoryName(targetPath);
                if (directories.Add(directory))
                {
                    FileUtilities.CreateDirectory(directory);
                }

                filesToCopy.Add(new KeyValuePair<string, string>(sourcePath, targetPath));
            }

            // Because some deployments use virtualized vpak, using a very parallelized copy is beneficial
            Parallel.ForEach(
                filesToCopy,
                new ParallelOptions()
                {
                    MaxDegreeOfParallelism = 50,
                },
                (fileToCopy) =>
                {
                    if (File.Exists(fileToCopy.Key))
                    {
                        File.Copy(fileToCopy.Key, fileToCopy.Value);
                    }
                });

#if !FEATURE_CORECLR
            var ngenExe = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), @"ngen.exe");
            var destExe = Path.Combine(destDir, System.AppDomain.CurrentDomain.FriendlyName);

            // queue:1 means it runs in the background
            var ngenArgs = "install " + destExe + " /queue:1";
            bool runNgen = File.Exists(ngenExe);
            if (runNgen)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(ngenExe, ngenArgs);
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                Process.Start(startInfo);
            }
#endif
            using (var file = new StreamWriter(File.OpenWrite(serverDeploymentHashFile)))
            {
                file.WriteLine(clientApp.TimestampBasedHash);

                // This isn't actually consumed. It is only used for debugging
                file.WriteLine("Debug info:");
#if !FEATURE_CORECLR
                if (runNgen)
                {
                    file.WriteLine("Ran Ngen: " + ngenExe + " " + ngenArgs);
                }
#endif
                file.WriteLine(clientApp.TimestampBasedHashDebug);
            }

            ServerDeploymentCacheCreated cacheCreated = default(ServerDeploymentCacheCreated);
            cacheCreated.TimeToCreateServerCacheMilliseconds = st.ElapsedMilliseconds;

            return cacheCreated;
        }

        public static string ComputeDeploymentDir(string serverDeploymentRoot)
        {
            // Note that this always creates a subdirectory even if there is an externally configured serverDeplymentRoot.
            // This is for protection in case the config provided path is a directory that already contains other files since
            // the server deployment will delete any files already existing in that directory.
            return Path.Combine(
                !string.IsNullOrWhiteSpace(serverDeploymentRoot) ? serverDeploymentRoot : Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
                ServerDeploymentDirectoryCache);
        }

        /// <summary>
        /// Kills the server mode BuildXL associated with this build instance
        /// </summary>
        internal static void KillServer(string serverDeploymentRoot)
        {
            // Check if the main root process (likely bxl.exe) is in use before attempting to delete, so we avoid partially deleting files
            // Not completey bullet proof (there can be a race) but it is highly unlikely the process starts to be in use right after this check
            Assembly rootAssembly = Assembly.GetEntryAssembly();
            Contract.Assert(rootAssembly != null, "Could not look up entry assembly");

            string assemblyFullPath = Path.Combine(serverDeploymentRoot, new FileInfo(AssemblyHelper.GetAssemblyLocation(rootAssembly)).Name);

            // Try kill process using Process.Kill.
            var killProcessResult = TryKillProcess(assemblyFullPath);

            if (!killProcessResult.Succeeded)
            {
                // Try kill process using wmci. Note that wmci is going to be deprecated, but it's been used here for a long time.
                var killProcessWithWMICResult = TryKillProcessWithWMIC(assemblyFullPath);

                if (!killProcessWithWMICResult.Succeeded)
                {
                    throw killProcessWithWMICResult.Failure.Annotate(killProcessResult.Failure.DescribeIncludingInnerFailures()).Throw();
                }
            }
        }

        private static Possible<Unit> TryKillProcessWithWMIC(string assemblyFullPath)
        {
            // We make sure there is no server process running. Observe that if there was one, it can't
            // be doing a build since in this case the client binaries were overridden, which means they are not locked
            // So here we should be killing a server process that is about to timeout and die anyway
            string args = string.Format(CultureInfo.InvariantCulture, KillBuildXLServerCommandLineArgs, assemblyFullPath);

            // wmic needs escaped backslashes
            args = args.Replace("\\", "\\\\");

            var killServer = new ProcessStartInfo(KillBuildXLServerCommandLine, args);
            killServer.WindowStyle = ProcessWindowStyle.Hidden;

            Process process = null;

            try
            {
                process = Process.Start(killServer);
                process.WaitForExit();
                return Unit.Void;
            }
            catch (Exception e)
            {
                return new Failure<string>(I($"Failed to kill process with path '{assemblyFullPath}'"), new Failure<Exception>(e));
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }
        }

        private static Possible<Unit> TryKillProcess(string assemblyFullPath)
        {
            string processName = Path.GetFileNameWithoutExtension(assemblyFullPath);

            foreach (var processToKill in Process.GetProcessesByName(processName).Where(p => string.Equals(assemblyFullPath, p.MainModule.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    if (!processToKill.HasExited)
                    {
                        processToKill.Kill();
                        processToKill.WaitForExit(3000);
                    }
                }
                catch (Exception e) when (
                    e is System.ComponentModel.Win32Exception
                    || e is NotSupportedException
                    || e is InvalidOperationException
                    || e is SystemException)
                {
                    return new Failure<string>(I($"Failed to kill process with name '{processName}' (process id: {processToKill.Id}) and path '{assemblyFullPath}'"), new Failure<Exception>(e));
                }
            }

            return Unit.Void;
        }

        /// <summary>
        /// Poisons the server deployment to cause it to be redeployed
        /// </summary>
        internal static void PoisonServerDeployment(string serverDeploymentRoot)
        {
            FileUtilities.DeleteFile(Path.Combine(ComputeDeploymentDir(serverDeploymentRoot), ServerDeploymentHashFilename), true);
        }

        /// <summary>
        /// Returns a hash of the content of the BuildXL binaries deployment cache directory.
        /// The deployment cache has to be created before calling this function
        /// </summary>
        [SuppressMessage("Microsoft.Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "StreamReader/StreamWriter takes ownership for disposal.")]
        public static string GetDeploymentCacheHash(string deploymentDir)
        {
            const string UnknownHash = "ServerUnknown";
            string filename = Path.Combine(deploymentDir, ServerDeploymentHashFilename);
            if (!File.Exists(filename))
            {
                return UnknownHash;
            }

            try
            {
                using (var file = new StreamReader(File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.Delete)))
                {
                    string hash = file.ReadLine();
                    return hash;
                }
            }
            catch (FileNotFoundException)
            {
                return UnknownHash;
            }
            catch (DirectoryNotFoundException)
            {
                return UnknownHash;
            }
            catch (IOException)
            {
                return UnknownHash;
            }
        }
    }
}