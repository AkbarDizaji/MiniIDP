# Mini IDP

A miniature **Internal Developer Platform** you run on your own machine. Open the dashboard, type a
service name, click **Create Service**, and Mini IDP will:

1. generate a .NET API from a bundled template,
2. build a Docker image from it,
3. run it as a container on a free `127.0.0.1` port,
4. wait for its `/health` endpoint, then show its URL, status and logs.

Everything is real: real `docker build`, real containers, real logs. It is an educational project,
so the code favors being small and easy to follow over being feature-complete.

> **Scope:** local only. Mini IDP does not create Git repositories, CI pipelines, cloud
> infrastructure or Kubernetes resources. Everything stays on your machine.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (`dotnet --version` → `10.x`)
- Docker Desktop or Docker Engine, running (`docker version` shows a *Server* section)
- macOS, Linux or Windows. The commands below use a Unix shell.

## Run it

```bash
dotnet run --project MiniIdp
```

Then open **http://127.0.0.1:5180**.

The first service takes a minute or two because Docker downloads the .NET SDK and runtime images. You
can download them in advance:

```bash
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/aspnet:10.0
```

Run the tests (these use a fake Docker, so they don't need a Docker daemon):

```bash
dotnet test
```

## Demo walkthrough

1. **Create `orders-api`.** Watch the progress bar move through Queued → Generating → Building →
   Starting → Running. When it's ready, click the `http://127.0.0.1:<port>` link and you'll see:
   `{"service":"orders-api","message":"Hello from orders-api!"}`
2. **View Logs.** *Provisioning* shows what the platform did (template generation and the full
   `docker build` output). *Container* shows the service's own request log (latest 200 lines).
   Refresh the service URL a few times and watch new request lines appear.
3. **Create `billing-api`.** It gets its own container and a different port.
4. **Delete `orders-api`.** Its container, image, generated code and logs are removed.
   `billing-api` keeps running.
5. **Restart the platform** (Ctrl+C, then `dotnet run --project MiniIdp` again). `billing-api` is
   still shown as Running, because its container never stopped. Try `docker stop <container>` while
   the platform is down: after a restart, the service shows as *Stopped*.
6. **Clean up** by deleting each service in the dashboard.

Through the API instead:

```bash
curl -X POST http://127.0.0.1:5180/api/services -H 'Content-Type: application/json' -d '{"name":"orders-api"}'
curl http://127.0.0.1:5180/api/services
curl http://127.0.0.1:5180/api/services/<id>/logs
curl -X DELETE http://127.0.0.1:5180/api/services/<id>
```

| Endpoint | Purpose |
|---|---|
| `POST /api/services` | `{"name": "..."}` → `202 Accepted` with the queued service (400 invalid name, 409 duplicate) |
| `GET /api/services` | All services, newest first |
| `GET /api/services/{id}` | One service |
| `GET /api/services/{id}/logs` | `{ provisioning, runtime, runtimeMessage }` |
| `DELETE /api/services/{id}` | 204 when done; 409 while provisioning; 500 if cleanup failed (record kept so you can retry) |

## Architecture

```
Browser ──HTTP──▶ Program.cs (Minimal API) ──▶ SQLite (Services table = the queue)
                                                       ▲
                       ProvisioningWorker (BackgroundService) polls for Queued rows
                                                       │
                       ServiceProvisioner ── copies Templates/dotnet-api ──▶ App_Data/workspaces/{id}
                                          └─ DockerCli ──▶ docker build / run / inspect / logs / rm
```

| File | Role |
|---|---|
| `MiniIdp/Program.cs` | Configuration and the five HTTP endpoints |
| `MiniIdp/Models/ServiceRecord.cs` | Service data, statuses, name rule, Docker names |
| `MiniIdp/Data/PlatformDbContext.cs` | SQLite setup and the unique name index |
| `MiniIdp/Services/ProvisioningWorker.cs` | Background loop: restart recovery, then process the queue |
| `MiniIdp/Services/ServiceProvisioner.cs` | Generate, build, run, health check, cleanup, delete, logs |
| `MiniIdp/Services/DockerCli.cs` | Runs the `docker` executable safely, with timeouts |
| `MiniIdp/Templates/dotnet-api/` | The template every service is generated from |
| `MiniIdp/wwwroot/` | Dashboard (plain HTML/CSS/JS) |
| `MiniIdp.Tests/` | Tests for validation, duplicates, failures, deletion and restart |

Runtime data lives in `MiniIdp/App_Data/`: the database `mini-idp.db`, generated code in
`workspaces/{id}/`, and provisioning logs in `logs/{id}.log`.

**Docker resources.** Each service owns exactly one image (`mini-idp/<name>-<short-id>:latest`) and
one container (`mini-idp-<name>-<short-id>`). Both carry the labels `mini-idp.managed=true` and
`mini-idp.service-id=<id>`. Cleanup finds containers by that label, so it also works when
provisioning crashed before the container ID was saved. Mini IDP never runs `docker prune` and never
touches resources it didn't create. List what it owns with:

```bash
docker ps -a --filter label=mini-idp.managed=true
docker images --filter label=mini-idp.managed=true
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| Service fails with *Could not start 'docker'* | Docker isn't on `PATH` for the platform process. Set `Platform:DockerPath` in `MiniIdp/appsettings.json` to the full path (e.g. `/usr/local/bin/docker`). |
| *Cannot connect to the Docker daemon* in the provisioning log | Start Docker Desktop and create the service again (delete the failed one first). |
| First build is slow or times out | Pre-pull the base images (see above). The build timeout is `Platform:BuildTimeoutSeconds` (default 600). |
| *Health check did not pass* | Check the container output copied into the provisioning log. The timeout is `Platform:HealthCheckTimeoutSeconds`. |
| Port 5180 already in use | `dotnet run --project MiniIdp --urls http://127.0.0.1:5200` |
| Start completely fresh | Delete your services in the dashboard, stop the platform, then delete `MiniIdp/App_Data/`. |
| Delete returns *Cleanup failed* | The record is kept on purpose. Fix the Docker problem shown and press Delete again. |
| Disk usage grows over time | Docker keeps build cache and base images. Mini IDP doesn't prune them; reclaim the space yourself with `docker builder prune` if you want to. |

## Deliberate limitations

- One service is provisioned at a time; others wait as Queued.
- Running services are reconciled with Docker only at platform startup, not continuously.
- No authentication: the dashboard and services listen on `127.0.0.1` only.
- `EnsureCreated()` instead of EF Core migrations.
- One built-in template; there is no way to upload code or choose another template.
