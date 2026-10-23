#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Units;

namespace TiaMcp.Openness.V21
{
    internal sealed partial class OpennessSession
    {
        private sealed class TechnologyReferenceReader
        {
            private readonly Project _project;
            private readonly string _plcPath;
            private readonly ScanBudget _budget;
            private readonly int _maximum;
            private readonly Dictionary<string, IEngineeringObject> _identities = new Dictionary<string, IEngineeringObject>(StringComparer.Ordinal);
            private ObjectIdentifierProvider _provider;
            private string _providerStatus = "notRequested", _projectPath;
            private bool _collision;
            private Dictionary<DeviceItem, string> _hardwarePaths;
            private string _hardwareMembership = "notRequested";

            internal TechnologyReferenceReader(Project project, string plcPath, int maximum = 50000)
            { _project = project; _plcPath = plcPath; _maximum = maximum; _budget = new ScanBudget(maximum); }

            private sealed class ReferenceFailure : Exception
            {
                internal readonly string Reason;
                internal ReferenceFailure(string reason) { Reason = reason; }
            }

            private T Read<T>(Func<T> read)
            {
                if (!_budget.Take()) throw new ReferenceFailure("readLimit");
                return read();
            }

            private static string Text(string value)
            {
                if (value != null)
                {
                    if (value.Length > 4096) throw new ReferenceFailure("textLimit");
                    try { new UTF8Encoding(false, true).GetByteCount(value); }
                    catch (EncoderFallbackException) { throw new ReferenceFailure("invalidText"); }
                }
                return value;
            }

            private ObjectIdentifierProvider Provider()
            {
                if (_providerStatus == "notRequested")
                {
                    _providerStatus = "readError";
                    _projectPath = Text(Read(() => _project.Path?.FullName));
                    if (string.IsNullOrWhiteSpace(_projectPath) || string.IsNullOrWhiteSpace(Text(_plcPath)))
                        throw new ReferenceFailure("missingProjectContext");
                    _provider = Read(() => _project.GetService<ObjectIdentifierProvider>());
                    _providerStatus = _provider == null ? "notExposed" : "available";
                }
                if (_provider == null) throw new ReferenceFailure("providerUnavailable");
                return _provider;
            }

            private string Identifier(IEngineeringObject target)
            {
                var provider = Provider();
                string id = Text(Read(() => provider.GetIdentifier(target)));
                if (string.IsNullOrWhiteSpace(id)) throw new ReferenceFailure("missingIdentifier");
                var found = Read(() => provider.Find(id));
                if (found == null || !Read(() => Equals(found, target))) throw new ReferenceFailure("roundTripMismatch");
                if (!string.Equals(id, Text(Read(() => provider.GetIdentifier(found))), StringComparison.Ordinal) ||
                    !string.Equals(id, Text(Read(() => provider.GetIdentifier(target))), StringComparison.Ordinal))
                    throw new ReferenceFailure("identifierDrift");
                if (_identities.TryGetValue(id, out var previous) && !Read(() => Equals(previous, target)))
                { _collision = true; throw new ReferenceFailure("identifierCollision"); }
                _identities[id] = target;
                return id;
            }

            private JObject Reference(string kind, string id, object value) => new JObject {
                ["valueSemantics"] = "engineeringReference", ["valueStatus"] = "capturedReference", ["valueComplete"] = true,
                ["identityComplete"] = true, ["resolution"] = "resolved", ["kind"] = kind,
                ["engineeringType"] = value.GetType().FullName, ["nativeIdentifier"] = id == null ? JValue.CreateNull() : new JValue(id),
                ["projectPath"] = _projectPath, ["selectionPlcPath"] = _plcPath,
                ["identityBasis"] = "projectProviderRoundTrip", ["persistenceVerified"] = false };

            private JObject Hardware(DeviceItem item)
            {
                var result = Reference("hardware", Identifier(item), item);
                if (_hardwarePaths == null)
                {
                    _hardwareMembership = "readError";
                    _hardwarePaths = Read(() => DeviceCompilation.Items(_project)).ToDictionary(entry => entry.Item, entry => entry.Path);
                    _hardwareMembership = "complete";
                }
                if (!_hardwarePaths.TryGetValue(item, out var path)) throw new ReferenceFailure("hardwareNotInProject");
                result["name"] = Text(Read(() => item.Name)); result["positionNumber"] = Read(() => item.PositionNumber);
                result["typeIdentifier"] = Text(Read(() => item.TypeIdentifier));
                if (!path.EndsWith("/items/" + Uri.EscapeDataString(result.Value<string>("name")), StringComparison.Ordinal))
                    throw new ReferenceFailure("hardwarePathDrift");
                result["hardwarePath"] = Text(path);
                result["hardwarePathBasis"] = "boundedProjectHardwareMembership";
                return result;
            }

            private JObject ChannelReference(Channel channel)
            {
                var owner = Read(() => channel.Parent) as DeviceItem;
                if (owner == null) throw new ReferenceFailure("missingChannelOwner");
                var parent = Hardware(owner);
                object number = Read(() => (object)channel.Number);
                string type = Text(Read(() => channel.Type).ToString()), ioType = Text(Read(() => channel.IoType).ToString());
                int identities = 0, selectors = 0;
                var channels = Read(() => owner.Channels);
                if (channels == null) throw new ReferenceFailure("missingChannelComposition");
                using (var iterator = Read(() => channels.GetEnumerator()))
                {
                    while (Read(() => iterator.MoveNext()))
                    {
                        var candidate = Read(() => iterator.Current);
                        if (candidate == null) throw new ReferenceFailure("missingChannelEntry");
                        bool same = Read(() => Equals(candidate, channel));
                        bool selector = Equals(Read(() => (object)candidate.Number), number) &&
                            Text(Read(() => candidate.Type).ToString()) == type && Text(Read(() => candidate.IoType).ToString()) == ioType;
                        if (same) identities++;
                        if (selector) selectors++;
                        if (same && !selector) throw new ReferenceFailure("channelSelectorDrift");
                        if (identities > 1 || selectors > 1) throw new ReferenceFailure("ambiguousChannel");
                    }
                }
                if (identities != 1 || selectors != 1 || !Read(() => Equals(channel.Parent, owner)))
                    throw new ReferenceFailure("channelOwnerMismatch");
                object container = Read(() => owner.Container);
                if (container is DeviceItem module && Read(() => Equals(module, owner))) throw new ReferenceFailure("cyclicHardwareOwner");
                parent["container"] = container is DeviceItem containerItem ? Hardware(containerItem) : null;
                var result = Reference("channel", null, channel);
                result["identityBasis"] = "ownerProviderRoundTripAndUniqueNativeChannel";
                result["parent"] = parent; result["number"] = InspectionValue(number);
                result["type"] = type; result["ioType"] = ioType;
                return result;
            }

            private JObject Resolve(object value)
            {
                string kind = value is Channel ? "channel" : value is DeviceItem ? "hardware" : value is PlcTag ? "tag" :
                    value is PlcBlock ? "block" : value is PlcUnitBase ? "softwareUnit" : null;
                if (kind == null) return null;
                try
                {
                    if (value is Channel channel) return ChannelReference(channel);
                    if (value is DeviceItem hardware) return Hardware(hardware);
                    return Reference(kind, Identifier((IEngineeringObject)value), value);
                }
                catch (Exception error)
                {
                    return new JObject { ["valueSemantics"] = "engineeringReference", ["valueStatus"] = "unresolvedReference",
                        ["valueComplete"] = false, ["identityComplete"] = false, ["resolution"] = "unresolved", ["kind"] = kind,
                        ["engineeringType"] = value.GetType().FullName, ["reason"] = error is ReferenceFailure failure ? failure.Reason : "readError",
                        ["persistenceVerified"] = false };
                }
            }

            internal JToken Render(object value, out bool complete) => InspectionValue(value, out complete, Resolve);
            internal JObject Coverage() => new JObject { ["providerStatus"] = _providerStatus, ["projectPath"] = _projectPath,
                ["selectionPlcPath"] = _plcPath, ["visitedReads"] = _budget.Used, ["maximumReads"] = _maximum,
                ["truncated"] = _budget.Truncated, ["identifierCollisionDetected"] = _collision,
                ["hardwareMembershipStatus"] = _hardwareMembership, ["hardwareItemsVisited"] = _hardwarePaths?.Count ?? 0,
                ["hardwareItemsLimit"] = 50000,
                ["persistenceVerified"] = false, ["atomicSnapshot"] = false };
        }
    }
}
