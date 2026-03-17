# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY EchoDeck.sln .
COPY src/EchoDeck.Core/EchoDeck.Core.csproj src/EchoDeck.Core/
COPY src/EchoDeck.Mcp/EchoDeck.Mcp.csproj src/EchoDeck.Mcp/
RUN dotnet restore
COPY . .
RUN dotnet publish src/EchoDeck.Mcp -c Release -o /app

# Runtime stage — Playwright base image includes Chromium
FROM mcr.microsoft.com/playwright/dotnet:v1.49.0-noble AS runtime
WORKDIR /app
COPY --from=build /app .

# Install FFmpeg and LibreOffice (with MS core fonts for fidelity)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        ffmpeg \
        libreoffice \
        ttf-mscorefonts-installer \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV SLIDE_RENDERER=officeOnline
ENTRYPOINT ["dotnet", "EchoDeck.Mcp.dll"]
