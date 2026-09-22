# Build stage: the SDK image compiles and publishes, and never ships to production.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# The csproj files are copied on their own first so the restore layer stays cached whenever only
# source files change.
COPY MarsRoverPhotos.sln ./
COPY src/MarsRoverPhotos.Core/MarsRoverPhotos.Core.csproj src/MarsRoverPhotos.Core/
COPY src/MarsRoverPhotos.Cli/MarsRoverPhotos.Cli.csproj src/MarsRoverPhotos.Cli/
COPY src/MarsRoverPhotos.Api/MarsRoverPhotos.Api.csproj src/MarsRoverPhotos.Api/
COPY tests/MarsRoverPhotos.Tests/MarsRoverPhotos.Tests.csproj tests/MarsRoverPhotos.Tests/
RUN dotnet restore src/MarsRoverPhotos.Api/MarsRoverPhotos.Api.csproj

COPY . .
RUN dotnet publish src/MarsRoverPhotos.Api/MarsRoverPhotos.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage: ASP.NET only, no SDK and no source.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

# The photo folder has to exist and be writable before the process drops to the non-root user,
# because the app only creates it relative to the content root at startup.
RUN mkdir -p /app/photos && chown -R app:app /app

# The aspnet image ships a non-root "app" user (uid 1654). Running as it keeps the container from
# writing anywhere it was not given explicitly.
USER app

EXPOSE 8080

# Downloaded images are data, not part of the image. Mount a host folder or a named volume here to
# keep them across container restarts.
VOLUME ["/app/photos"]

# The NASA key is never baked in. Pass it at run time, for example:
#   docker build -t mars-rover-photos .
#   docker run --rm -p 8080:8080 -e NASA_API_KEY=your_key -v mars-photos:/app/photos mars-rover-photos
# Without it the app falls back to DEMO_KEY, which NASA rate limits hard.
ENTRYPOINT ["dotnet", "MarsRoverPhotos.Api.dll"]
