using TiaMcp.Openness.V21;

namespace TiaMcp.Tests
{
    internal static class SimulationOnlineLoginTests
    {
        internal static Task OnlyTheOfferedProjectUserLoginOnce()
        {
            const string login = "OnlineAuthenticationConfiguration";
            Check.Equal(SimulationOnlineLogin.ProjectUser, SimulationOnlineLogin.Decide(login, true, false, true));
            // A second login challenge, a missing credential or a CPU without project users stays unanswered.
            Check.Equal(SimulationOnlineLogin.Unanswered, SimulationOnlineLogin.Decide(login, true, true, true));
            Check.Equal(SimulationOnlineLogin.Unanswered, SimulationOnlineLogin.Decide(login, false, false, true));
            Check.Equal(SimulationOnlineLogin.Unanswered, SimulationOnlineLogin.Decide(login, true, false, false));
            // Passwords, TLS and every other configuration are never answered with the login.
            foreach (string other in new[] { "OnlinePasswordConfiguration", "OnlineReadAccessPassword", "TlsVerificationConfiguration", "UploadPasswordConfiguration", "unknown" })
                Check.Equal(SimulationOnlineLogin.Unanswered, SimulationOnlineLogin.Decide(other, true, false, true));
            return Task.CompletedTask;
        }
    }
}
