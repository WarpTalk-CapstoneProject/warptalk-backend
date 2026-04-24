# WarpTalk — Task Checklist

## Architecture Design
- [x] Read capstone register requirements
- [x] Design 5-repo architecture (consolidated from 9)
- [x] Define inter-service communication (gRPC + Redis Streams)
- [x] Architecture audit vs requirements

## Database Schema
- [x] Design 33 tables across 5 schemas
- [x] Install database-schema-designer skill
- [x] Gap analysis (12 gaps found)
- [x] Apply all gap fixes (indexes, FK, CHECK, NOT NULL, partitioning, FTS, UUID v7)
- [x] Add user_settings table

## Full Audit (Speed + Scale + Deploy)
- [x] Speed audit (4 issues: PgBouncer, Redis cache, AI streaming, read replicas)
- [x] Scalability audit (4 issues: horizontal scaling, GPU isolation, auto-scaling, rate limits)
- [x] Deployment audit (7 issues: health checks, secrets, backup, monitoring, CORS, migrations, TURN HA)
- [x] Apply all 15 fixes into implementation_plan.md

## Next: Scaffolding
- [ ] Create init-db.sh script (PostgreSQL + PgBouncer + schemas)
- [ ] Create docker-compose.yml (full stack)
- [ ] Scaffold warptalk-backend (.NET 8 solution)
- [ ] Scaffold warptalk-ai (Python workers)
- [ ] Scaffold warptalk-web (NextJS)
- [ ] Scaffold warptalk-desktop (ElectronJS)
- [ ] Scaffold warptalk-infrastructure
