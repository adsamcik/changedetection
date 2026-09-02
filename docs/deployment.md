# Deployment and operations

## Supported topology

Run exactly one ChangeDetection application instance against each LiteDB database. LiteDB is an embedded local database; do not place the database on NFS/SMB storage and do not mount the same database into multiple replicas.

The application serves HTTP on container port `8080`. Terminate TLS at a reverse proxy and keep the application port on a private network. The supplied Compose file instead publishes it only on host loopback for a single-machine installation.

## Authentication modes

`SingleUser` is the default and has no authentication challenge. It is suitable only when the listening socket is loopback-only or otherwise protected by a trusted access layer.

For shared or remotely reachable deployments, use `SSO` mode behind an authenticating reverse proxy:

```yaml
services:
  app:
    environment:
      Authentication__Mode: SSO
      Authentication__AdminGroup: changedetection-admins
      Authentication__TrustedProxies__0: 172.16.0.0/12
```

The proxy must remove identity headers supplied by clients, authenticate the request, and then set these defaults:

| Header | Meaning |
| --- | --- |
| `Remote-User` | stable username; required |
| `Remote-Email` | email address |
| `Remote-Name` | display name |
| `Remote-Groups` | comma-separated group names |

Set `Authentication__TrustedProxies__0`, `__1`, and so on to the exact proxy IPs or CIDR ranges. Do not broadly trust the public client network. Header names can be changed through the matching `Authentication__UsernameHeader`, `EmailHeader`, `DisplayNameHeader`, and `GroupsHeader` settings.

## Secrets

Inject search-provider credentials as environment variables from the deployment platform's secret manager. Avoid committing credentials, baking them into the image, or storing a populated `.env` on a production host. Provider settings entered through the UI are encrypted using the ASP.NET Core Data Protection key ring.

Persist and restrict `/keys` separately from `/data`. The database and key ring are both needed to recover encrypted credentials, but keeping their backups separate prevents a database-only disclosure from also revealing the decryption material. See [Security configuration](security.md) for the exact settings.

## Data, backup, and restore

The application writes its live database to `LiteDb__Path` and creates scheduled or on-demand backups under its content root's `backups` directory. In the image these map to `/data/changedetection.db` and `/app/backups`.

For a recoverable backup set, capture:

1. A completed application-created `.db` backup from `/app/backups`.
2. A separate protected backup of `/keys`.
3. The deployed image tag or source revision and all non-secret configuration.

Do not copy the live LiteDB file while the application is writing it. Use the application's backup function, or stop the container before copying the data volume.

To restore, stop the application, preserve the current volumes, restore the database and matching key ring, then start the same application version and verify `/health` before upgrading.

## Upgrades and rollback

Before an upgrade:

1. Record the current image tag or Git revision.
2. Create and export an application database backup.
3. Back up the Data Protection key ring independently.
4. Build or pull the new image, then run `docker compose up -d`.
5. Wait for `docker compose ps` to report `healthy` and inspect `docker compose logs app`.

If verification fails, stop the new container, restore the prior image tag, database, and matching key ring, then start the prior version. Database migrations can make a data file unsuitable for an older build, so changing only the image is not a complete rollback.

## Resource and runtime notes

- Browser checks use Chromium. The image includes the package-matched browser and uses a 1 GB shared-memory allocation.
- Give the container at least 2 GB of memory; complex pages and concurrent checks benefit from more.
- `/tmp` is the only writable temporary filesystem in the hardened Compose service. Persistent writes belong in the three mounted volumes.
- The `/health` endpoint verifies LiteDB access and is appropriate for container health checks.
- Use `docker compose logs --follow app` for runtime diagnosis. API keys are excluded from API responses, but logs and exported diagnostics should still be handled as sensitive operational data.
