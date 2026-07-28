# Hotfix: Keep embedding workers alive while Redis starts

Date: 2026-07-26
Reporter: WarpTalk integration test

## Bug

The `embedding` and `embedding-search` workers exit during startup with
`Error -2 connecting to redis:6379. Name or service not known`. Docker then
restarts the container repeatedly, so the indexing worker never consumes
`embedding:index_requests` and cannot push documents to Qdrant.

## Root Cause

`RedisStreamClient.connect()` performs one unprotected `PING`. Redis is owned by
the backend Compose project and is attached through an external network, so its
DNS record may not exist yet when the AI worker starts. The AI Compose file also
used the container name instead of the backend service DNS alias.

## Fix

- Retry Redis startup `PING` with bounded exponential backoff while keeping the
  worker process alive.
- Use the shared network service alias `redis` in the AI Compose configuration.

## Verification

- Regression test proves a transient Redis `PING` failure is retried.
- Render the Compose configuration and recreate the embedding service.
- Confirm `redis_connected`, `consume_loop_started`, no restart storm, and a
  document indexing result in Qdrant.

## Regression Risk

An invalid Redis endpoint or credential can now keep a worker waiting instead of
exiting. Authentication and non-connection Redis errors remain fail-fast; the
startup retry is limited to connection, timeout, and OS-level network errors.
