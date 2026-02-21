# Simple Office Scheduler

An office hours scheduling application built with ASP.NET Core 8 Blazor Server, SQLite, and FullCalendar.

## Prerequisites

- .NET 8 SDK
- Node.js (for building TypeScript/FullCalendar)
- EF Core tools: `dotnet tool install --global dotnet-ef`

## Quick Start

```bash
cd src/SimpleOfficeScheduler
dotnet run
```

The app will start on `http://localhost:5000`. Login with:
- **Username:** `testadmin`
- **Password:** `Test123!`

## Features

- Create and manage office hour events (one-time or recurring)
- Browse events with search and weekly calendar view (FullCalendar)
- Sign up for events with capacity enforcement
- Cancel specific instances of recurring events
- Adjust event schedule and recurrence
- Transfer event ownership
- Active Directory (LDAP) authentication
- Microsoft Teams calendar invites via Graph API

## Configuration

All configuration is in `appsettings.json`:

- **ActiveDirectory**: LDAP connection settings (disabled in Development)
- **GraphApi**: Microsoft Graph API credentials for Teams calendar integration
- **SeedUser**: Test user credentials (enabled in Development)
- **Recurrence**: Horizon settings for recurring event expansion

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
