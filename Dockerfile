# ---- Build Stage ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

# Install Node.js for FullCalendar TypeScript build
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# Copy solution and project files for restore
COPY SimpleOfficeScheduler.sln ./
COPY src/SimpleOfficeScheduler/SimpleOfficeScheduler.csproj src/SimpleOfficeScheduler/
COPY tests/SimpleOfficeScheduler.Tests/SimpleOfficeScheduler.Tests.csproj tests/SimpleOfficeScheduler.Tests/

# Restore NuGet packages
RUN dotnet restore

# Copy everything else
COPY . .

# Install npm dependencies and build TypeScript
WORKDIR /src/src/SimpleOfficeScheduler/ClientApp
RUN npm ci

# Build and publish the .NET app
WORKDIR /src
RUN dotnet publish src/SimpleOfficeScheduler/SimpleOfficeScheduler.csproj \
    -c Release \
    -o /app/publish

# Verify publish manifest exists (required by MapStaticAssets for .NET 10)
RUN test -f /app/publish/SimpleOfficeScheduler.staticwebassets.endpoints.json \
    || (echo "FATAL: static assets manifest missing from publish output" && exit 1)

# ---- Runtime Stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

WORKDIR /app

# Copy published app from build stage
COPY --from=build /app/publish .

# Create data directory for SQLite
RUN mkdir -p /app/data

# Set environment variables
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV ConnectionStrings__DefaultConnection="Data Source=/app/data/officeScheduler.db"

EXPOSE 8080

ENTRYPOINT ["dotnet", "SimpleOfficeScheduler.dll"]
