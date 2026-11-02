# Video walkthrough: following one request through Mini IDP

This script follows a single click on **Create Service** through the code, in the order things
actually happen. Each section names the file and function to show on screen, and what to point out.

---

## 1. The dashboard: `MiniIdp/wwwroot/`

**Show:** `index.html`, then `app.js`.

- `index.html` is static markup: a form, an empty list, a logs panel, and a `<template>` for one
  service card. There's no framework and no build step; ASP.NET serves these files from `wwwroot`.
- `app.js` → the `submit` handler. It checks the name against the same regex the server uses (for
  quick feedback only; the server is the authority), disables the button so a double click can't
  send two requests, and `POST`s `{ name }`.
- `poll()` at the bottom calls `GET /api/services` every 2 seconds. It uses `setTimeout` after each
  round instead of `setInterval`, so a slow response never causes overlapping requests.
- `updateCard()` changes text and classes **in place** instead of re-rendering. That's why the
  input keeps focus and the log panel keeps its scroll position while the page refreshes every 2 s.
- The progress bar is derived only from `service.status`. The browser invents nothing.

> **Talking point:** the UI is just a client of a small JSON API. Everything below could be driven
> with `curl` as well.

## 2. The create endpoint: `MiniIdp/Program.cs`

**Show:** `api.MapPost("/", ...)`.

1. Validate the name with `ServiceRecord.IsValidName`. Invalid names get a 400.
2. Check for an existing service with that name. Duplicates get a 409.
3. Insert a row with status `Queued`, and catch `DbUpdateException` in case two requests raced past
   the check at the same moment.
4. Return **202 Accepted**, not 201 Created: the request has been *accepted for processing*, and
   the work hasn't happened yet.

The endpoint does not build anything. It finishes in milliseconds, and the slow work happens in the
background.

## 3. The database record: `Models/ServiceRecord.cs` and `Data/PlatformDbContext.cs`

**Show:** `ServiceRecord`, then `OnModelCreating`.

- `ServiceStatus` holds the whole lifecycle: Queued → Generating → Building → Starting → Running,
  plus Failed and Stopped.
- The name regex uses `\z` instead of `$`. In .NET, `$` also matches *before a trailing newline*,
  so `"orders-api\n"` would pass. A test checks exactly this.
- `ImageTag` and `ContainerName` are **computed from the ID**, not stored. If the platform crashes
  between `docker run` and saving the container ID, we can still work out which resources belong to
  this service.
- `HasIndex(s => s.Name).IsUnique()` is the real duplicate guarantee. The API check only gives a
  friendlier error message.
- Statuses are stored as text, so the table is readable with any SQLite viewer.

> **Talking point:** this table is also the job queue. A `Queued` row *is* the job. One fewer moving
> part than adding RabbitMQ or a Channel.

## 4. The worker: `Services/ProvisioningWorker.cs`

**Show:** `ExecuteAsync`, then `ProvisionNextQueuedServiceAsync`.

- A `BackgroundService` starts together with the web app.
- On start, it runs restart recovery first (section 9).
- Then it loops: find the oldest `Queued` row, provision it, repeat. It only sleeps when the queue
  is empty.
- **Fresh DI scope per job:** the worker is a singleton, but `DbContext` is scoped and not
  thread-safe. `scopeFactory.CreateScope()` gives each job its own context.
- Exactly one worker processing one job at a time means two builds never fight each other, and no
  locking is needed.

## 5. Template generation: `ServiceProvisioner.GenerateFromTemplate`

**Show:** `Templates/dotnet-api/` first, then `GenerateFromTemplate`.

- The template is a normal, compilable .NET project. The name appears as
  `const string ServiceName = "__SERVICE_NAME__";`. It's a plain string, so the template builds
  even before replacement, and a single `string.Replace` is all generation needs.
- Files are copied into `App_Data/workspaces/{service-id}/`. The path is built from a GUID, never
  from user input.
- The template's `Dockerfile` is multi-stage: build with the SDK image, run on the much smaller
  ASP.NET runtime image as a non-root user. `.dockerignore` keeps `bin/` and `obj/` out of the
  build context.

## 6. Docker build and run: `BuildImageAsync`, `StartContainerAsync`, `DockerCli.RunAsync`

**Show:** `DockerCli.RunAsync` first.

- `ProcessStartInfo` with `UseShellExecute = false` and **`ArgumentList`**: each argument goes to
  Docker as a separate string. No shell, no string concatenation, nothing to inject into.
- stdout and stderr are read **concurrently** through events. Reading one stream after the other can
  deadlock when the process fills the other stream's buffer.
- A linked `CancellationTokenSource` adds a timeout. On timeout or shutdown, the docker process is
  killed instead of being left running.
- `RunAsync` is `virtual` for one reason: the tests substitute a fake Docker.

**Then show** `BuildImageAsync` and `StartContainerAsync`:

- `--label mini-idp.service-id=<id>` goes on both the image and the container. Labels mark
  ownership.
- `onOutputLine` streams every build line into the provisioning log while the build is still
  running, which is why the dashboard shows live build output.
- `--publish 127.0.0.1::8080` means "bind to localhost only, and let Docker pick a free host port".
  That's how several services run side by side without port clashes.
- `GetHostPortAsync` asks `docker inspect` which port Docker picked.

## 7. Health check: `WaitUntilHealthyAsync`

- Poll `http://127.0.0.1:<port>/health` every 500 ms until it returns 2xx or the timeout expires.
- The status only becomes **Running after the check passes**. A container that starts but crashes,
  or never listens, does not count as running.
- If anything fails, `FailAsync` runs:
  1. copy the container's last output into the provisioning log (it's about to be deleted),
  2. remove the container and image this service owns, and its workspace,
  3. set `Failed` with a readable `ErrorMessage`. The **log file is kept** for diagnosis.

## 8. Logs: `GET /api/services/{id}/logs`

There are two separate sources, and they answer two different questions:

| Source | Where it comes from | Answers |
|---|---|---|
| Provisioning | `App_Data/logs/{id}.log`, written by the platform | "What did the platform do, and why did it fail?" |
| Runtime | `docker logs --tail 200 <container>` | "What is my service doing right now?" |

The runtime log is capped at 200 lines so a busy service can't flood the browser. The template's
small request-logging middleware writes one line per request, so you can refresh the service URL
and watch the lines appear.

## 9. Deletion: `MapDelete` and `ServiceProvisioner.DeleteAsync`

- While a service is being provisioned, `DELETE` returns **409**. Deleting halfway through a build
  would race the worker. Refusing is simple and honest.
- `RemoveOwnedResourcesAsync` finds containers **by label**, removes them with `rm --force`, then
  removes only this service's exact image tag. There is no `prune` and no wildcard, so other
  services are untouched.
- The database row is removed **last**. If Docker cleanup throws, the row stays and the user can
  press Delete again. That behavior is covered by a test.

**Restart recovery** (`RecoverAfterRestartAsync`) reuses the same pieces:

- interrupted provisioning (Generating/Building/Starting) → `FailAsync` (which also cleans up),
- Running/Stopped → ask Docker for the real container state and update the row,
- Queued → left alone, so the worker simply carries on,
- nothing is ever rebuilt automatically.

---

## Educational tradeoffs (worth saying on camera)

| Choice here | What a production platform would do | Why it's fine here |
|---|---|---|
| SQLite table as queue, polled every second | A message broker or a transactional outbox | One process, one worker: polling is simple and visible |
| One job at a time | Parallel workers with leases/locking | No concurrency bugs to explain |
| Docker CLI via `Process` | Docker Engine API / SDK, or Kubernetes | The commands on screen are the ones you'd type yourself |
| `EnsureCreated()` | EF Core migrations | Schema never changes in the demo |
| Reconcile only at startup | Continuous reconciliation loop | Keeps the worker short |
| Status refresh by polling every 2 s | WebSockets / SignalR | Zero extra moving parts |
| No auth, localhost only | SSO, RBAC, audit log | Nothing listens beyond `127.0.0.1` |

**Local-only scope:** Mini IDP doesn't create Git repositories, CI pipelines, cloud resources or
Kubernetes objects. It demonstrates the core IDP idea, *a golden-path template turned into a running
service with one click*, in a codebase you can read in one sitting.

## Suggested demo order on camera

1. `dotnet run --project MiniIdp`, open http://127.0.0.1:5180
2. Create `orders-api` → watch the steps → click its URL
3. View Logs → Provisioning, then Container; refresh the service URL and watch request lines appear
4. Create `billing-api` → point out the different port
5. Try creating `orders-api` again → inline "already exists" error
6. Delete `orders-api` → `billing-api` still answers
7. Ctrl+C, run again → state is reconciled, nothing rebuilds
8. Run `docker ps --filter label=mini-idp.managed=true` to show the labels
