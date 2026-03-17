# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/EchoDeck.Core/EchoDeck.Core.csproj src/EchoDeck.Core/
COPY src/EchoDeck.Mcp/EchoDeck.Mcp.csproj src/EchoDeck.Mcp/
RUN dotnet restore src/EchoDeck.Mcp/EchoDeck.Mcp.csproj
COPY . .
RUN dotnet publish src/EchoDeck.Mcp -c Release -o /app

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app
COPY --from=build /app .

# Install PowerShell (needed to run playwright.ps1), FFmpeg, LibreOffice, font deps
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        wget apt-transport-https software-properties-common \
    && wget -q "https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb" \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        powershell \
        ffmpeg \
        libreoffice \
        fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

# Install Playwright's Chromium + all its OS dependencies
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN pwsh /app/playwright.ps1 install chromium --with-deps

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV SLIDE_RENDERER=officeOnline
ENV DATA_DIR=/data
ENTRYPOINT ["dotnet", "EchoDeck.Mcp.dll"]
