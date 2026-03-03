# BUILD STAGE
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# 1. Create the skeleton structure for restoration
# This ensures NuGet packages are cached
COPY ["sec-scraper.csproj", "Scraper/"]
COPY ["UserRegistration/Backend/FocusDB/FocusDB.csproj", "UserRegistration/Backend/FocusDB/"]
COPY ["UserRegistration/Backend/FocusLib/FocusLib.csproj", "UserRegistration/Backend/FocusLib/"]

RUN dotnet restore "Scraper/sec-scraper.csproj"

# 2. Copy the actual source code into the correct folders
# Copy the scraper's .cs files into the Scraper folder
COPY [".", "Scraper/"]

# Copy the Backend project source code
COPY ["UserRegistration/Backend/FocusDB/", "UserRegistration/Backend/FocusDB/"]
COPY ["UserRegistration/Backend/FocusLib/", "UserRegistration/Backend/FocusLib/"]

# 3. Publish from the Scraper directory
WORKDIR /app/Scraper
RUN dotnet publish "sec-scraper.csproj" -c Release -o /publish --no-restore

# RUNTIME STAGE
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /publish .

# Install dependencies for the bundled Chromium
RUN apt-get update && apt-get install -y \
    wget \
    gnupg \
    ca-certificates \
    # Standard Chrome dependencies
    libnss3 \
    libatk-bridge2.0-0 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libpangocairo-1.0-0 \
    libxshmfence1 \
    # ADD THESE NEW ONES FOR MODERN REVISIONS:
    libatk1.0-0 \
    libc6 \
    libcairo2 \
    libcups2 \
    libdbus-1-3 \
    libexpat1 \
    libfontconfig1 \
    libgcc1 \
    libgconf-2-4 \
    libgdk-pixbuf2.0-0 \
    libglib2.0-0 \
    libgtk-3-0 \
    libnspr4 \
    libpango-1.0-0 \
    libstdc++6 \
    libx11-6 \
    libx11-xcb1 \
    libxcb1 \
    libxcursor1 \
    libxext6 \
    libxfixes3 \
    libxi6 \
    libxrender1 \
    libxss1 \
    libxtst6 \
    --no-install-recommends && \
    rm -rf /var/lib/apt/lists/*

ENV CHROME_PATH=/usr/bin/chromium
ENTRYPOINT ["dotnet", "sec-scraper.dll"]
