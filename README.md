# Simple Office Scheduler

[![CI](https://github.com/wilstoff/simple_office_scheduler/actions/workflows/ci.yml/badge.svg)](https://github.com/wilstoff/simple_office_scheduler/actions/workflows/ci.yml)

An office scheduling application built with ASP.NET Core 10 Blazor Server, SQLite, and FullCalendar.

## Quick Start (Docker)

Pull the pre-built image from GitHub Container Registry:

```bash
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -e ActiveDirectory__Enabled=false \
  -e SeedUser__Enabled=true \
  ghcr.io/wilstoff/simple_office_scheduler:latest
```

Or build locally:

```bash
docker build -t simple-office-scheduler .
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -e ActiveDirectory__Enabled=false \
  -e SeedUser__Enabled=true \
  simple-office-scheduler
```

Open http://localhost:8080 and login with:
- **Username:** `testadmin`
- **Password:** `Test123!`

## Configuration

All settings can be overridden with environment variables using the `__` (double underscore) separator. Pass them with `docker run -e`:

### Authentication

| Variable | Default | Description |
|----------|---------|-------------|
| `ActiveDirectory__Enabled` | `true` | Set `false` for local auth (no LDAP server required) |
| `ActiveDirectory__Host` | `ldap.company.com` | LDAP server hostname |
| `ActiveDirectory__Port` | `389` | LDAP port |
| `ActiveDirectory__UseSsl` | `false` | Use SSL for LDAP connection |
| `ActiveDirectory__Domain` | `COMPANY` | AD domain name |
| `ActiveDirectory__SearchBase` | `DC=company,DC=com` | LDAP search base DN |
| `ActiveDirectory__ServiceAccountDn` | *(empty)* | Service account DN for user search (e.g. `CN=svc_scheduler,OU=Service Accounts,DC=company,DC=com`) |
| `ActiveDirectory__ServiceAccountPassword` | *(empty)* | Service account password for user search |

### Teams Calendar Integration (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `GraphApi__TenantId` | *(empty)* | Azure AD tenant ID |
| `GraphApi__ClientId` | *(empty)* | Azure app registration client ID |
| `GraphApi__ClientSecret` | *(empty)* | Client secret (application permissions mode) |
| `GraphApi__ServiceAccountEmail` | *(empty)* | Service account email (delegated permissions mode) |
| `GraphApi__ServiceAccountPassword` | *(empty)* | Service account password (delegated permissions mode) |

Two authentication modes are supported:

- **Delegated (ROPC)**: Set `ServiceAccountEmail` + `ServiceAccountPassword`. The app authenticates as the service account and creates meetings on its own calendar, adding event participants as attendees. Only requires Delegated `Calendars.ReadWrite` permission — no admin consent needed. The service account must be MFA-exempt and the app registration must have "Allow public client flows" enabled.
- **Application**: Set `ClientSecret` instead. The app creates meetings directly on event owners' calendars using Application `Calendars.ReadWrite` permission. Requires admin consent.

When neither mode is configured, calendar invite functionality is disabled.

### Seed User

| Variable | Default | Description |
|----------|---------|-------------|
| `SeedUser__Enabled` | `false` | Create a default user on startup |
| `SeedUser__Username` | `testadmin` | Seed user username |
| `SeedUser__Password` | `Test123!` | Seed user password |
| `SeedUser__DisplayName` | `Test Admin` | Seed user display name |
| `SeedUser__Email` | `testadmin@localhost` | Seed user email |

### Other

| Variable | Default | Description |
|----------|---------|-------------|
| `ConnectionStrings__DefaultConnection` | `Data Source=/app/data/officeScheduler.db` | SQLite connection string |
| `Recurrence__DefaultHorizonMonths` | `6` | Months ahead to expand recurring events |
| `Timezone__DefaultTimeZoneId` | `America/Chicago` | Default IANA timezone |

### Production Example (with Active Directory + Teams Calendar)

```bash
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -e ActiveDirectory__Host=ldap.mycompany.com \
  -e ActiveDirectory__Domain=MYCOMPANY \
  -e ActiveDirectory__SearchBase="DC=mycompany,DC=com" \
  -e ActiveDirectory__ServiceAccountDn="CN=svc_scheduler,OU=Service Accounts,DC=mycompany,DC=com" \
  -e ActiveDirectory__ServiceAccountPassword="s3cret" \
  -e GraphApi__TenantId="your-tenant-id" \
  -e GraphApi__ClientId="your-client-id" \
  -e GraphApi__ServiceAccountEmail="scheduler@mycompany.com" \
  -e GraphApi__ServiceAccountPassword="svc-password" \
  ghcr.io/wilstoff/simple_office_scheduler:latest
```

### Alternative: Mount a Config File

Instead of environment variables, mount a custom appsettings file:

```bash
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -v ./my-appsettings.json:/app/appsettings.Production.json:ro \
  ghcr.io/wilstoff/simple_office_scheduler:latest
```

## Development Setup

### Prerequisites

- .NET 10 SDK
- Node.js 20+

### Run Locally

```bash
cd src/SimpleOfficeScheduler
dotnet run
```

The app starts on `http://localhost:5000` with the Development profile (AD disabled, seed user enabled).

### Run Tests

```bash
# Install Playwright browsers (first time only)
dotnet build tests/SimpleOfficeScheduler.Tests
dotnet tool install --global Microsoft.Playwright.CLI
playwright install chromium

# Run all tests
dotnet test
```

## Features

- Create and manage events (one-time or recurring)
- Browse events with search and weekly calendar view (FullCalendar)
- Sign up for events with capacity enforcement
- Cancel specific instances of recurring events
- Adjust event schedule and recurrence
- Transfer event ownership with searchable user lookup (local DB + Active Directory)
- Active Directory (LDAP) authentication
- Microsoft Teams calendar invites via Graph API
- Light/dark theme with per-user persistence
- Per-user timezone settings

## Project Structure

```
src/SimpleOfficeScheduler/
  Models/          - Entity models (AppUser, Event, EventOccurrence, EventSignup)
  Data/            - EF Core DbContext and database seeder
  Services/        - Business logic (events, auth, calendar, recurrence)
  Auth/            - Blazor authentication state provider
  Components/      - Blazor pages and layout
  Controllers/     - API endpoints (calendar feed, events, user search, auth)
  ClientApp/       - TypeScript source for FullCalendar interop
```
