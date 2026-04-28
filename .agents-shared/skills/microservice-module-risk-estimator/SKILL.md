---
name: microservice-module-risk-estimator
description: Estimate feasibility, delivery risk, and budget impact when adding one or more microservice modules to the current system. Use when the user asks whether adding a module is feasible, how much percentage load or cost it adds, how risky it is before UAT, or wants a repeatable formula for microservice expansion planning.
---

# Microservice Module Risk Estimator

Use this skill when the user wants a reusable way to estimate the impact of adding a new microservice module to the current architecture.

Assume the goal is practical planning, not theoretical perfection. Prefer percentage-based estimation unless the user gives exact cloud prices.

## Inputs to collect

Ask only for missing items that materially affect the estimate:

- Remaining weeks before UAT or release
- Team composition and effective backend capacity
- Remaining budget or monthly budget ceiling
- Whether infra is shared node pool or dedicated runtime
- Estimated module size: `small`, `medium`, or `large`
- Integration level: `low`, `medium`, or `high`
- UAT impact: `low`, `medium`, or `high`

If the user does not know exact numbers, use the default assumptions below and state them explicitly.

## Default assumptions

For this project family, use:

- `effective_backend_capacity = 2.0 to 2.2 BE`
- reserve `30%` for integration/hardening
- reserve `20%` for bugfix/UAT support
- feature-safe capacity:

```text
feature_safe_capacity = remaining_backend_capacity * 50%
```

If weeks are known:

```text
remaining_backend_capacity = remaining_weeks * effective_backend_capacity
feature_safe_capacity = remaining_backend_capacity * 0.5
```

## Module sizing model

Classify the new module into one of these buckets.

### Small

- simple CRUD or thin service extraction
- few dependencies
- no complex new data pipeline
- `0.8 to 1.5 dev-week`
- `5% to 10%` infra increase
- `8% to 12%` total project load increase

### Medium

- touches multiple services
- may include realtime, worker, or background processing
- has its own schema or data flow
- `1.5 to 2.5 dev-week`
- `10% to 18%` infra increase
- `12% to 22%` total project load increase

### Large

- data-heavy, pipeline-heavy, or cross-cutting
- difficult integration or test surface
- new index/store/queue/search/media subsystem
- `2.5 to 4.0 dev-week`
- `18% to 35%` infra increase
- `20% to 35%` total project load increase

## Core formulas

Use these formulas in the response.

### 1. Delivery increase

```text
delivery_increase_percent
= (module_dev_week / feature_safe_capacity) * 100
```

### 2. Infra increase

If exact cloud prices are unknown, estimate by share:

```text
infra_increase_percent
= module_infra_bucket_percent
```

Use the size bucket defaults unless the user provides better numbers.

### 3. Overall project load increase

```text
project_load_increase_percent
= max(delivery_increase_percent, module_load_bucket_percent)
```

This avoids underestimating when the team is the true bottleneck.

### 4. Feasibility score

```text
feasibility_score
= 100
- 40 * (module_dev_week / feature_safe_capacity)
- 30 * (module_infra_cost / remaining_budget)
- 15 * integration_factor
- 15 * uat_risk_factor
```

Use these factors:

- `low = 0.5`
- `medium = 1.0`
- `high = 1.5`

If `module_infra_cost` is unknown, substitute the percent form:

```text
budget_pressure_ratio = infra_increase_percent / allowed_budget_growth_percent
```

Then use:

```text
feasibility_score
= 100
- 40 * (module_dev_week / feature_safe_capacity)
- 30 * budget_pressure_ratio
- 15 * integration_factor
- 15 * uat_risk_factor
```

## Decision thresholds

- `> 80`: Feasible
- `65 to 80`: Feasible if scope is trimmed
- `50 to 64`: Risky
- `< 50`: No-go

Also report a short traffic-light label:

- `Green`: low execution risk
- `Yellow`: manageable but needs scope discipline
- `Red`: high risk to timeline or budget

## Quick rule of thumb

When the user wants a fast answer:

- `1 small module` adds about `10%` project load
- `1 medium module` adds about `18%` project load
- `1 large module` adds about `30%` project load

Before UAT, try to keep:

```text
total_added_project_load <= 35% to 40%
```

## Output format

Answer with:

1. assumptions used
2. module size classification
3. estimated `% infra increase`
4. estimated `% delivery/load increase`
5. feasibility score
6. verdict: `Feasible`, `Risky`, or `No-go`
7. one short recommendation to reduce risk

Keep the answer compact unless the user asks for a full report.

## Project-specific note

For WarpTalk-like systems with shared cluster infrastructure, do not estimate a new module as a full new server by default. Treat it as:

```text
new_module_cost
= shared_cluster_resource_share
+ direct_data_or_service_cost
+ distributed_system_overhead
```

Distributed system overhead includes:

- service integration
- CI/CD and config
- logging and monitoring
- retries and failure handling
- extra UAT surface
