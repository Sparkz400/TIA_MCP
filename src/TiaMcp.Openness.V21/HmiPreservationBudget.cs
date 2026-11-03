namespace TiaMcp.Model
{
    // Covers every HMI in the project, independently of the individual XML bound.
    internal static class HmiPreservationBudget
    {
        internal const int MaxArtifacts = 2048;
        internal const long MaxArtifactBytes = HmiPreservationRules.MaxDocumentBytes;
        internal const long MaxTotalBytes = 512L * 1024 * 1024;
        internal const string LimitMessage = "HMI preservation exceeds 2048 native artifacts, 64 MiB per artifact or 512 MiB in total; no truncated scope authorizes mutation.";

        internal static bool AllowsAddition(int count, long totalBytes, long artifactBytes)
        {
            return count >= 0 && count < MaxArtifacts && totalBytes >= 0 && totalBytes <= MaxTotalBytes
                && artifactBytes >= 0 && artifactBytes <= MaxArtifactBytes
                && artifactBytes <= MaxTotalBytes - totalBytes;
        }
    }
}
