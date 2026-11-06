# 0002. One PostgreSQL server with a database and a role per service

## Status

Accepted

## Date

2026-11-05

## Context

Each service reads and writes only its own database (`AGENTS.md`). The local stack must make that rule hold without every developer remembering it, and it must fit in the memory budget: the whole stack stays under 12 GB, and an idle PostgreSQL container costs tens of megabytes before it holds any data.

## Decision

The stack runs one PostgreSQL server. `deploy/compose/postgres/init.sql` creates, for each service, a login role, a database owned by that role, and `REVOKE ALL ON DATABASE … FROM PUBLIC`. The revoke matters because PostgreSQL grants `CONNECT` on every new database to `PUBLIC`, so without it any role could connect to any database.

## Alternatives considered

- **One PostgreSQL container per service.** The isolation is total, and each service can pin its own version and settings. But it adds a container, a port and a volume per service, and the map of ports grows with every service. Compose gets heavier for a rule the database can already enforce.
- **One database, one schema per service.** A role per service, `REVOKE … ON SCHEMA … FROM PUBLIC` and grants on its own schema only can enforce the boundary too ([privileges](https://www.postgresql.org/docs/current/ddl-priv.html)). It loses on three costs. The services share one migration history, one extension set and one backup and restore unit. The boundary is one permission mistake away, for example a broad `GRANT … ON ALL TABLES`. And extracting a service later means moving its data, not changing a connection string.

## Consequences

- A service's role can't connect to another service's database. A wrong connection string fails with a permission error, not with silently wrong data.
- All services share one server's CPU, memory and `max_connections`. A noisy service can slow the others. That is fine locally, and the tests use their own containers.
- Each new service adds the same three lines to `init.sql`. PostgreSQL runs init scripts only on an empty data volume, so after editing the file run `docker compose down -v`.
- In AWS, one RDS instance can use the same roles and databases, and a service that needs its own instance can move later: its connection string is the only thing that changes. One gotcha: to run `CREATE DATABASE … OWNER catalog`, the creating role must be able to `SET ROLE` to `catalog` ([CREATE DATABASE](https://www.postgresql.org/docs/current/sql-createdatabase.html)). The RDS master user isn't a superuser, so run `GRANT catalog TO <master user>` first. If we are wrong and one service needs different server settings, that split is the cost.
- **Connection budget.** Every service with a database opens at most 20 connections in its request pool and 2 in its readiness pool (ADR 0001), 22 in all. The six services with a database need 132, and `compose.yaml` raises `max_connections` to 200 (the default is typically 100, per the [PostgreSQL docs](https://www.postgresql.org/docs/current/runtime-config-connection.html)), which leaves room for migration jobs and `psql`. A new service with a database adds 22 to the sum. Task 3's k6 run confirms the pending-request and timeout metrics stay at 0 timeouts.
