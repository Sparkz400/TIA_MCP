namespace TiaMcp.Openness.V21
{
    /// <summary>
    /// When an owned-PLCSIM online route answers a challenge with the instance's generated runtime user: only the login
    /// challenge, only once, and only when the CPU offers project-user authentication. Everything else stays unanswered
    /// and is latched as a failure by the caller.
    /// </summary>
    internal static class SimulationOnlineLogin
    {
        internal const string ProjectUser = "ProjectUser", Unanswered = "unanswered";

        internal static string Decide(string requestType, bool loginSupplied, bool alreadyAnswered, bool projectUserOffered) =>
            requestType == "OnlineAuthenticationConfiguration" && loginSupplied && !alreadyAnswered && projectUserOffered ? ProjectUser : Unanswered;
    }
}
