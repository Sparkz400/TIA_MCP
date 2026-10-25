## Do I need TIA Portal installed?

Only for native work: opening projects, fresh exports, compiling, applying changes and simulation. Document workflows (OPC UA NodeSets, AutomationML/CAEX, GRAPH XML, translation workbooks, saved project reviews, VCI Git) and the development checks run without Siemens software. See [[Getting Started]].

## Which versions are supported?

TIA Portal **V21** with Openness and PLCSIM Advanced **V8**. Other versions are not supported.

## Can I use it on a production PLC or plant?

No. Use it only with offline projects, copies, scratch projects and simulators you own. The simulation workflow operates on adapter-owned PLCSIM instances and does not expose physical PLC download, force or CPU control.

## Is it production-ready?

No, it is pre-1.0. Offline tooling is validated in CI; live TIA Portal and PLCSIM runtime qualification is incomplete. [Current status](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/STATUS.md) records what is verified and with which evidence.

## How do I find out what a feature supports?

Ask the server: `feature_catalog` lists implementation scope, `target_capabilities` reports what the selected target supports. The [engineering coverage](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/reference/engineering-coverage.md) reference covers native changes.

## What if a change fails halfway?

Changes are not transactions across Siemens calls. Each applied change has a checkpoint and journal for recovery; see the [engineering workflow](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/engineering-workflows.md).

## The server cannot find TIA Portal or Openness

Run `.\scripts\Probe-Environment.ps1` and follow [environment diagnostics](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/environment-diagnostics.md) and [Openness registration](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/guides/openness-application-registration.md). Your Windows user needs the `Siemens TIA Openness` group, and `PlcSimUsers` for simulation.

## Is this a Siemens product?

No. TIA MCP is independent and not affiliated with, endorsed or supported by Siemens AG. Siemens, TIA Portal, SIMATIC, STEP 7, WinCC and PLCSIM are trademarks of Siemens AG.

## What is the license?

[Apache License 2.0](https://github.com/Sparkz400/TIA_MCP/blob/main/LICENSE); keep the attribution in [NOTICE](https://github.com/Sparkz400/TIA_MCP/blob/main/NOTICE). Siemens products and SDK assemblies stay under their own terms.

Supplied vendor libraries keep their own notices and prerequisites. The
[PAL/DMC review](https://github.com/Sparkz400/TIA_MCP/blob/main/docs/verification/vendor-intake-review-20261025.md)
records the reviewed packages and exclusions; they are not bundled with TIA MCP by that review.

## Where do I ask something that is not answered here?

[Discussions → Q&A](https://github.com/Sparkz400/TIA_MCP/discussions/categories/q-a).
