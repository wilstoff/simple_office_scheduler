# Simple Office Scheduler

[![CI](https://github.com/wilstoff/simple_office_scheduler/actions/workflows/ci.yml/badge.svg)](https://github.com/wilstoff/simple_office_scheduler/actions/workflows/ci.yml)

An office hours scheduling application built with ASP.NET Core 8 Blazor Server, SQLite, and FullCalendar.

## Quick Start (Docker)

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

### Teams Calendar Integration (optional)

| Variable | Default | Description |
|----------|---------|-------------|
| `GraphApi__TenantId` | *(empty)* | Azure AD tenant ID |
| `GraphApi__ClientId` | *(empty)* | Graph API application client ID |
| `GraphApi__ClientSecret` | *(empty)* | Graph API client secret |

When Graph API credentials are not set, calendar invite functionality is disabled.

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

### Production Example (with Active Directory)

```bash
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -e ActiveDirectory__Host=ldap.mycompany.com \
  -e ActiveDirectory__Domain=MYCOMPANY \
  -e ActiveDirectory__SearchBase="DC=mycompany,DC=com" \
  simple-office-scheduler
```

### Alternative: Mount a Config File

Instead of environment variables, mount a custom appsettings file:

```bash
docker run -d -p 8080:8080 \
  -v scheduler-data:/app/data \
  -v ./my-appsettings.json:/app/appsettings.Production.json:ro \
  simple-office-scheduler
```

## Development Setup

### Prerequisites

- .NET 8 SDK
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
pwsh tests/SimpleOfficeScheduler.Tests/bin/Debug/net8.0/playwright.ps1 install chromium

# Run all tests
dotnet test
```

## Features

- Create and manage office hour events (one-time or recurring)
- Browse events with search and weekly calendar view (FullCalendar)
- Sign up for events with capacity enforcement
- Cancel specific instances of recurring events
- Adjust event schedule and recurrence
- Transfer event ownership
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
  Controllers/     - API endpoint for FullCalendar JSON feed
  ClientApp/       - TypeScript source for FullCalendar interop
```
