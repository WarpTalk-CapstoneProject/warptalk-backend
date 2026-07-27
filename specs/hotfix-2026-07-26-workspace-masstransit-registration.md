# Hotfix: Register MassTransit in Workspace Service

Date: 2026-07-26
Reporter: Docker startup verification

## Bug

The Workspace Service exits during startup, so document download requests cannot reach the workspace backend.

## Root Cause

`HybridWorkspaceEventPublisher` depends on MassTransit's `IPublishEndpoint`, but the Workspace Service host does not call the shared `AddWarpTalkMassTransit` registration before building its service provider.

## Fix

Register the shared WarpTalk MassTransit configuration in `Program.cs`. Keep the existing RabbitMQ settings and event publisher implementations unchanged.

## Verification

- Build and run the Workspace Service image.
- Confirm the container remains running without an `IPublishEndpoint` dependency-resolution exception.
- Run the targeted document download backend test.
- Verified: workspace image builds, container remains Up, root endpoint returns HTTP 200, and the targeted download test passes (1/1).

## Regression Risk

Low. This activates the already-required shared MassTransit registration and does not alter document download or event payload contracts.
