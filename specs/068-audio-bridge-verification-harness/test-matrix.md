# WT-131 Verification Matrix

| Check ID | Scope | Type | Feasible Automation | Reason | Pass Criteria |
|---|---|---|---|---|---|
| VM-01 | Service health readiness | Automated | Yes | API/process checks are deterministic | Required services report healthy |
| VM-02 | Device role visibility | Hybrid | Partial | Device enumeration automatable, role correctness still operator-validated | All 4 required roles are mapped correctly |
| VM-03 | Local-to-remote path | Manual | No | End-to-end audio perception in external meeting app is OS/device dependent | Remote hears translated output + transcript updated |
| VM-04 | Remote-to-local path | Manual | No | Requires third-party participant audio and runtime device routing | Local user hears translated output + transcript updated |
| VM-05 | Loop prevention guard | Hybrid | Partial | Source-tag checks can be logged; audible loop detection remains manual | No self-feedback loop during path tests |
| VM-06 | Failure recovery (device/permission/pipeline) | Manual | No | Error injection is environment-specific and requires operator actions | Recovery steps restore known-safe state |
