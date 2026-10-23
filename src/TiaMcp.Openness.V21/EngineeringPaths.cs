#nullable disable
using System;
using System.IO;

namespace TiaMcp.Openness.V21
{
    internal static class EngineeringPaths
    {
        internal const int MaximumProjectDirectoryLength = 143;

        // TIA first creates an unpacked project directory named after the archive
        // (without .zap21), even when the final compressed file path is valid.
        internal static void ValidateProjectArchivePath(string directory, string archiveName)
        {
            if (!IsFullyQualifiedWindowsPath(directory) || string.IsNullOrWhiteSpace(archiveName)
                || archiveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || !archiveName.EndsWith(".zap21", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(archiveName))
                || Path.GetFileNameWithoutExtension(archiveName).TrimEnd(' ', '.') != Path.GetFileNameWithoutExtension(archiveName))
                throw new BridgeException("InvalidRequest", "Project archive requires an absolute directory and a single .zap21 file name.");
            string stagingDirectory = Path.Combine(Path.GetFullPath(directory), Path.GetFileNameWithoutExtension(archiveName));
            if (stagingDirectory.Length > MaximumProjectDirectoryLength)
                throw new BridgeException("ArchivePathTooLong", "TIA limits the archive staging directory to 143 characters. Configure a shorter artifacts directory; no project was saved or closed.");
        }

        internal static string NewRecoveryArchiveName(string directory)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string name = "a_" + Guid.NewGuid().ToString("N") + ".zap21";
                ValidateProjectArchivePath(directory, name);
                string staging = Path.Combine(directory, Path.GetFileNameWithoutExtension(name));
                string archive = Path.Combine(directory, name);
                if (!File.Exists(archive) && !Directory.Exists(archive) && !File.Exists(staging) && !Directory.Exists(staging)) return name;
            }
            throw new BridgeException("RecoveryPathConflict", "Could not select an unused recovery archive path.");
        }

        // Pure preflight: this method creates no directories and touches no TIA state.
        internal static string NewRecoveryDirectory(string artifactDirectory, string projectName)
        {
            if (!IsFullyQualifiedWindowsPath(artifactDirectory))
                throw new BridgeException("InvalidRequest", "Recovery artifactDirectory must be absolute.");
            if (string.IsNullOrWhiteSpace(projectName) || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || projectName == "." || projectName == "..")
                throw new BridgeException("InvalidRequest", "Recovery project name must be a single valid directory component.");
            string root = Path.GetFullPath(artifactDirectory);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string directory = Path.Combine(root, "r_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string projectedDirectory = Path.Combine(directory, projectName);
                if (projectedDirectory.Length > MaximumProjectDirectoryLength)
                    throw new BridgeException("RecoveryPathTooLong", "TIA limits the retrieved project directory to 143 characters. Configure a shorter artifacts directory before recovery; no project was saved or closed.");
                if (!Directory.Exists(directory) && !File.Exists(directory)) return directory;
            }
            throw new BridgeException("RecoveryPathConflict", "Could not select an unused recovery directory.");
        }

        private static bool IsFullyQualifiedWindowsPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) return true;
            if (!path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal)) return false;
            var components = path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return components.Length >= 2 && components[0] != "." && components[0] != ".." && components[1] != "." && components[1] != "..";
        }
    }
}
