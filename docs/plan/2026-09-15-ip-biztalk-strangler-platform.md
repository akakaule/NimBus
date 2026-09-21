# IP integration platform: BizTalk replacement by strangler

Builds on the 14 September investigation (`2026-09-14-integration-platform-investigation.md`,
`2026-09-14-on-prem-feasibility.md`, `2026-09-14-operator-and-system-design.md`). Those pages
covered a generic code-first platform, the on-prem transport and the operator console. This
pass adds the IP-specific angle the user asked for on 15 September 2026:

1. Replace on-premise BizTalk Server for IP with adapters, ports and mappings written as
   C# code, not configured in a designer.
2. Expose both an API gateway and messaging; NimBus is the messaging candidate.
3. Wrap the existing SOAP interfaces with a strangler pattern and reuse SoapCore the way
   the WF Proxy (`IP.ProxyWorkflow.API`) already does.

## Method

- Read the IP.WF2.0 repository (`c:\git\IP\IP.WF2.0`, branch `main`, HEAD `b67c997f`):
  `CLAUDE.md`, ADR-002 (proxy dual-connector), ADR-005 (service emulators), the wiki pages
  `Integrationer.md`, `External-Service-Integrations.md`, `Arkitektur.md`,
  `Ændringsforslag-til-systemlandskab.md`, `dokumentnotifikation.md`, plus a code-level
  sweep of the proxy, SoapCore wiring, WCF clients and BizTalk touchpoints.
- Verified the NimBus side: no SOAP or gateway code exists today (grep for SoapCore, YARP,
  strangler, gateway across `*.cs|*.csproj|*.props|*.md` returns nothing outside a skill
  reference). Adapter model from `docs/building-adapters.md`; handoff protocol from
  `NimBus.SDK/HandoffClient*.cs`.
- Checked external facts on 15 September 2026: SoapCore 1.2.1.16 (nuget, 11 Sep 2026,
  targets net8.0/netcoreapp3.1/netstandard2.0), CoreWCF.Http 1.9.1 (16 Jun 2026),
  System.ServiceModel.Http 10.0.652802 (Microsoft, 11 Nov 2025), YARP docs (auth per route,
  Windows/Negotiate identity does not flow to destinations), Azure API Management self-hosted
  gateway (Developer/Premium only, needs outbound 443 to the Azure control plane, fails
  static), BizTalk Server 2020 lifecycle (mainstream ends 11 Apr 2028, extended 9 Apr 2030),
  Microsoft's BizTalk feature mapping and wave-based migration guidance.

## Key findings that shaped the proposal

- Every BizTalk endpoint WF2 calls is a published orchestration
  (`http://bts-01/ser<Name>BTS.Published/WcfService_ser<Name>BTS_Orchestrations.svc`); BizTalk
  also serves a versioned `WsdlRepository`. IP's recovery-plan review counts twelve BizTalk
  integrations and calls the dependency underestimated.
- The SOAP layer has no transport auth; callers are identified by the IP `Header` element in
  the body (namespace `http://BTS.Common.Schemas.Header`), which the proxy maps to `x-api-key`
  plus `ip.initsystem.*` headers towards WF2. Outbound WF2 → BizTalk uses `BasicHttpBinding`
  with `TransportCredentialOnly` and Windows app-pool identity.
- WF2 has no message bus, no XSLT, no SFTP/FILE/EDI. Asynchrony is the Manager loop plus
  Hangfire. Roughly 43 workstep services call backends synchronously inside `AutomatiskStep`.
- Hosting is IIS in-process plus a Windows Service; Aspire is local-only; ADR-006 rejects
  Docker/Kubernetes. This rules out container-only gateways (APIM self-hosted, Kong) and favours
  a YARP host.
- SoapCore is referenced only from `IP.ProxyWorkflow.API.Common`; its `SoapMessage/` helpers
  (xsi/xsd body fix, `soap:` prefix envelope, logging, SOAPAction routing) are the reusable SOAP
  SDK. Pre-existing issues to fix rather than carry: two `BaseClientConfigurator` copies and a
  permissive `CertificateValidatorForTest` on the HTTPS path.
- No BizTalk WSDL/XSD is checked in; `ConnectedService.json` metadata is stale.

## Deliverable

- `docs/spec/ip-biztalk-strangler-platform.html`: standalone proposal with the verified IP
  landscape, a BizTalk-concept to code mapping, the three-plane target architecture
  (gateway, messaging, SOAP facade), the three strangler seams with the switch mechanisms
  IP already owns, the SoapCore versus CoreWCF decision rule, the gateway options, a wave
  planner over the twelve BizTalk-hosted services, risks, and pilot gates.
- Cross-link added from `code-first-integration-platform.html`.

## Limits

No BizTalk artifacts (orchestrations, maps, pipelines, bindings) were available in either
repository; the BizTalk feature inventory is inferred from what WF2 consumes and must be
confirmed by exporting the BizTalk applications. No code was implemented, installed or
benchmarked. Rendered browser QA of the HTML is limited to the single Artifact preview.
