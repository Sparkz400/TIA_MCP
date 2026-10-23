#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private static List<DeviceCompilation.Entry> EngineeringDeviceEntries(Project project) => DeviceCompilation.Entries(project);
        private static IEnumerable<Device> EngineeringDevices(Project project) => EngineeringDeviceEntries(project).Select(entry => entry.Item);
        private static JObject CompileDeviceProject(Project project, Action<string, string> journal = null) => DeviceCompilation.Compile(project, journal);
    }
}
