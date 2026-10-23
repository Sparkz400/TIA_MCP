# OPC UA runtime dependency notices

The isolated runtime module pins
[OPCFoundation.NetStandard.Opc.Ua.Client 1.5.378.182](https://www.nuget.org/packages/OPCFoundation.NetStandard.Opc.Ua.Client/1.5.378.182).
Its five runtime SDK packages resolve to the same version and upstream commit
`6cca32dd6d79c8b57d2e00e4d2071b991d2cf71b`.
The [maintenance release](https://github.com/OPCFoundation/UA-.NETStandard/releases/tag/1.5.378.182)
was published on 4 October 2026. Upstream also offers 2.x; its
[maintenance policy](https://github.com/OPCFoundation/UA-.NETStandard#-migrating-from-15378-to-20)
continues security and critical fixes for the 1.5.378 branch. The selected pin
uses that supported API line and does not claim to be the newest major version.

| Dependency | Version | Retained license | Distribution |
| --- | --- | --- | --- |
| OPC Foundation Client, Configuration, Core, Security.Certificates, Types | 1.5.378.182 | OPC Foundation MIT License 1.00 | Host runtime |
| BitFaster.Caching | 2.6.0 | MIT | Host runtime |
| Newtonsoft.Json | 13.0.4 | MIT | Host runtime |
| OPC Foundation Server | 1.5.378.182 | OPC Foundation MIT License 1.00 | Test project only |

[provenance.json](provenance.json) binds each original nuspec and license to the
exact official NuGet archive and repository commit. The retained bytes were
compared with fresh archive downloads. The separate Server nuspec documents the
qualification dependency; packaging rejects its package or binary in the Host.
`Package.ps1` derives the shipped inventory from the actual Host dependency graph
and requires exact version-matched notices for every package.

The official [NuGet vulnerability feed](https://api.nuget.org/v3/vulnerabilities/index.json)
reported no matching advisory ranges for the 11 Host packages and separate test
server when checked on 20 October 2026. The captured feed hashes and observation
time are retained in the provenance record. This is a point-in-time check.

The source audit binds the SDK's session, discovery and certificate-validation
code to the pinned commit. Integration tests still establish this client's
specific trust, identity, ownership and cleanup behavior. Upstream capabilities
and certification claims do not qualify this bridge or any physical controller.
