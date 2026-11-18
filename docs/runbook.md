# Clinical RAG operations runbook

This runbook covers deployment, dependency failures and the rehearsed recovery
paths. Use synthetic or de-identified data only. Never put bearer tokens, Colab
keys, pipeline keys, registration secrets, case text or raw upstream bodies in a
ticket, alert annotation, log query or report.

The executable rehearsal is
[`scripts/rehearse-deployment.ps1`](../scripts/rehearse-deployment.ps1); the
dated result is recorded in [operations.md](operations.md). Starting-point
alert rules are in [alert-rules.yml](alert-rules.yml).

## Alert triage

| Alert | First checks | Safe action |
| --- | --- | --- |
| `ClinicalRagReadyFailing` | `/health`, `/ready` response, the `checks.database`, `checks.colab`, `checks.pipeline` summary | Diagnose the failed dependency. Do not restart the API just because `/ready` is 503. |
| `ClinicalRag5xxRateHigh` | `http.server` error rate, sanitized request logs, dependency status | Isolate the dependency or bad deployment; preserve evidence; roll back only with an approved artifact. |
| `ClinicalRagP95LatencyHigh` | database waits, cache generation/reload, Full-Text/BM25 path, Colab/GPU saturation, concurrent chunk jobs | Reduce load or investigate the measured bottleneck; use the staging load test for tails. |
| `ClinicalRagColabReadyFailing` | `/api/operations/colab`, Colab `/ready`, heartbeat age, manual override, connection revision | Restart/register the Colab runtime and verify recovery without restarting the API. |

`/metrics` requires a platform admin or the `Operations:MetricsKey` value in
`X-Operations-Key`. The Prometheus histogram is
`clinicalrag_step_duration_milliseconds_*` with fixed `step` and `outcome`
labels. `/ready` failures are also observed with a blackbox probe
(`probe_success{job="clinical-rag-ready"}`); the histogram has no route label.
Configure that probe for the public `/ready` URL. The optional
`clinical-rag-colab-ready` probe must send the Colab `X-Colab-Key` from the
secret store; never put the key in the alert rule or scrape config checked into
this repository.

## Readiness failure (`/ready` is 503)

1. `GET /health` must remain 200. `/ready` returning 503 with `Retry-After: 30`
   means at least one dependency is not ready; it is not an instruction to
   bypass verification.
2. Read the safe `checks` object. It separates database, Colab/model and Python
   pipeline readiness. Do not expose upstream response bodies.
3. Database: verify SQL connectivity, the migration history and
   `ColabConnections` availability. If the database is down, stop writes and
   follow backup/restore; do not run destructive repair SQL.
4. Colab/model: check `/api/operations/colab`, the registered connection,
   heartbeat age (`Colab:HeartbeatStaleSeconds`), manual override and the Colab
   `/ready` endpoint. Use the notebook restart procedure below.
5. Pipeline: check the Python service `/health` and `/ready` with the service
   key. The ASP.NET API returns 503 while the verified pipeline is unavailable;
   it must not return an unverified answer.
6. Recheck `/ready` and preserve the request ID, timestamp and sanitized
   dependency result. No API restart is required for a Colab URL rotation or a
   recovered dependency.

## Colab restart, heartbeat and URL rotation

Detailed notebook steps are in
[live-test-setup.md](live-test-setup.md#notebook-restart--url-rotation-procedure).

- While Colab is down, `/ready` is **503** with `Retry-After: 30`; `/health`
  remains live.
- A runtime restart/relocation gets a new URL. Startup registration
  (`heartbeat=false`) writes the new URL, protected key and 32-character
  instance ID, increments the connection revision and updates
  `LastHeartbeatAt`. The same URL/key/instance can heartbeat without rotating.
- A different instance with `heartbeat=true` is rejected as **superseded**.
  Old heartbeats cannot restore the old URL.
- A manual override blocks notebook registration until an authorized owner
  clears it. Do not clear it merely to make a heartbeat pass; record why.
- The API reads the connection from the database on each logical call. After a
  successful registration, the next model/readiness call uses the new runtime
  without an ASP.NET restart.

## 5xx rate

1. Use the sanitized `http.server` outcome and request ID to identify the
   route/time; do not query request bodies or prompts.
2. A 503 from a dependency is a controlled unavailable response. A 500 is an
   unexpected application failure; preserve logs and the failing release.
3. Check database capacity/locks, pipeline readiness, Colab/GPU health and the
   current configuration/rule publication revision.
4. Do not retry a non-idempotent chunk submission or analysis request blindly.
   The Colab resilience handler retries only explicitly idempotent calls.
5. Roll back to the previous reviewed build and database/migration state only
   with the approved restore procedure. Never bypass `PipelineResultPolicy`,
   the Python verifier, owner-only publication or approved-rules-only.

## Latency (p95)

1. Separate HTTP latency from retrieval and model latency. The metric here is
   the ASP.NET `http.server` histogram; the staging load test measures retrieve
   and analyze percentiles separately.
2. Check whether the cache is cold/reloading after a chunk change. The durable
   `Workspaces.SearchGeneration` should match the cache entry; repeated
   generation changes indicate a write storm, not a cache bug.
3. Check SQL and Full-Text population/fallback. Full-Text population is
   asynchronous; an enabled index is not proof of current recall.
4. Check Colab GPU memory, concurrent chunk jobs and model queueing. Reduce
   nonessential chunk work before changing retrieval correctness settings.
5. Do not raise `minRerank`, `topK`, candidate limits or timeouts merely to make
   a latency alert disappear. Record any configuration publication and its
   approved test evidence.

## Cache coherence and scale-out

- `Chunks` has a SQL Server trigger `TR_Chunks_SearchGeneration`. It increments
  `Workspaces.SearchGeneration` atomically for every chunk insert/update/delete,
  including another API process or direct SQL.
- Each retrieval reads the workspace generation with one primary-key lookup
  before using the process-local immutable index. A different generation
  reloads; the same generation is safe to serve.
- `Chunk` uses EF `UseSqlOutputClause(false)` because SQL Server rejects the
  default OUTPUT clause on a trigger-bearing target. Do not remove that setting
  while the trigger exists.
- If the generation stops advancing, verify the trigger exists, the migration
  `SearchGenerationCoherence` is applied, and chunk writes are committing. Do
  not disable the trigger to work around EF errors; fix the EF/output mapping.
- Cache memory budget (`Retrieval:CacheSizeMegabytes`) and absolute expiry
  (`Retrieval:CacheSeconds`) remain guardrails, not correctness mechanisms.

## Migration, legacy archives and re-embedding

1. Drain old API instances and take a recoverable backup. Do not run old code
   against the binary-vector schema.
2. Apply the reviewed idempotent `db/schema.sql` with a migration/DBA principal.
   It preserves legacy JSON in `LegacyEmbeddingJson`, moves `embedded` chunks to
   `accepted`, moves `indexed` documents back to `review`, and releases
   interrupted embedding claims. It never treats archived JSON as binary
   float32.
3. Verify row counts, archive values (valid/empty/malformed/NULL), document
   status/revision, migration history and the search-generation trigger.
4. Re-embed through the normal editor-authorized endpoint with the current
   encoder. A committed explicit re-embed clears that chunk's archive by design;
   never delete archives in bulk or label old bytes as current vectors.
5. Do not insert passing records, set client-written status, or bypass the
   publication test gate to shorten the upgrade.

## Backup and restore drill

Run the rehearsal and record the date/outcome in
[operations.md](operations.md). The exact commands are documented and exercised
by `scripts/rehearse-deployment.ps1`; the core SQL is:

```sql
BACKUP DATABASE [ClinicalRag]
TO DISK = N'<approved-backup-path>\ClinicalRag-<timestamp>.bak'
WITH COPY_ONLY, CHECKSUM, INIT;

RESTORE VERIFYONLY FROM DISK = N'<approved-backup-path>\ClinicalRag-<timestamp>.bak';
RESTORE FILELISTONLY FROM DISK = N'<approved-backup-path>\ClinicalRag-<timestamp>.bak';
RESTORE DATABASE [ClinicalRagRestoreDrill]
FROM DISK = N'<approved-backup-path>\ClinicalRag-<timestamp>.bak'
WITH MOVE N'<logical-data-file>' TO N'<restore-path>\ClinicalRagRestoreDrill.mdf',
     MOVE N'<logical-log-file>'  TO N'<restore-path>\ClinicalRagRestoreDrill.ldf',
     RECOVERY, REPLACE;
```

For a real restore:

1. Stop/drain writes and identify the exact full backup + log chain. Use a
   separate disposable restore database first; never restore over live.
2. `RESTORE VERIFYONLY`, then restore `WITH RECOVERY` to the isolated database.
3. Verify row counts, schema migration history, document/chunk states and
   `LegacyEmbeddingJson` hashes/counts. Run a synthetic readiness/retrieval
   check, not a clinical case.
4. Measure backup and restore duration. Record RPO/RTO and the retained backup
   location. Encrypt and restrict backups; do not leave them on an
   unauthenticated share.
5. Only after independent approval, schedule the production switch. Keep the
   old backup until the post-restore checks pass.

## Secret rotation

All secrets live in the deployment secret store or Colab Secrets, never in the
repository, reports or command lines.

| Secret | Rotation procedure | Verification |
| --- | --- | --- |
| Colab API key (`X-Colab-Key`) | Generate in the secret manager, update Colab secret and the backend connection via startup registration; revoke the old key. | `/ready` 200; old key rejected; connection revision advanced; no API restart needed. |
| Colab registration secret (`X-Colab-Registration-Secret`) | Update the launcher/environment and Colab secret together; restart the configuration reader if it is process-scoped. | A registration with the old secret returns 401; startup registration still succeeds with the new one. |
| Pipeline service key (`X-Pipeline-Key`) | Update backend `Pipeline:ApiKey` and the Python service environment together; restart both processes. | Pipeline `/ready` with the new key is 200; old key is unauthorized; end-to-end analyze still verifies. |
| Database credentials | Rotate in the SQL/secret manager, update the connection string, restart/deploy; revoke the old principal after verification. | `/ready` database check 200; migrations/history readable; no plaintext credential in logs. |
| Firebase service credentials / admin access | Rotate through the provider console; update the secret store; remove old access. | Token validation and admin policy still work; old credential is rejected. |

Never rotate secrets during an incident without recording the old/new revision
and verification time. Do not paste the values into alerts, reports or chat.

## Full-Text setup and fallback

1. Run `db/enable-chunk-fulltext.sql` outside a transaction on a SQL Server
   instance with Full-Text installed. It refuses clearly when the component,
   schema or key is incompatible.
2. The script is idempotent: it preserves an existing catalog/key, adds missing
   `Text`/`Section`, enables the index and sets change tracking. Population is
   asynchronous; poll actual representative terms with the application
   principal before relying on exact-term recall.
3. If Full-Text is absent or an index disappears, retrieval uses BM25. Expected
   unavailable-index SQL errors fall back and invalidate capability; unexpected
   database/permission errors remain sanitized failures. Do not silence them.
4. Restart/refresh API processes after installing or changing an index that was
   previously detected as unavailable so capability and cache snapshots refresh.

## Rehearsal cadence

Run the migration/backup/Full-Text rehearsal before every schema or SQL Server
change, after any change to the search-generation trigger, and at least before
a release that changes vector storage or retrieval. Run the focused Colab
rotation/heartbeat tests before a notebook or connection-store change. Record
the date, command, result, timings and row counts in
[operations.md](operations.md).
