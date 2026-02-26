# BUILD STAGE
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

# We set the WORKDIR to /app, but we will place our code 
# in a subfolder so the relative ".." paths work.
WORKDIR /app

# 1. Create the folder structure
# This ensures that /app/Scraper and /app/UserRegistration exist side-by-side
COPY ["sec-scraper.csproj", "Scraper/"]
COPY ["UserRegistration/Backend/FocusDB/FocusDB.csproj", "UserRegistration/Backend/FocusDB/"]
COPY ["UserRegistration/Backend/FocusLib/FocusLib.csproj", "UserRegistration/Backend/FocusLib/"]

# 2. Move into the Scraper folder to restore
WORKDIR /app/Scraper
RUN dotnet restore "sec-scraper.csproj"

# 3. Copy the rest of the source code
# We go back to /app to copy everything into the right spots
WORKDIR /app
COPY . .

# 4. Publish from the Scraper directory
WORKDIR /app/Scraper
RUN dotnet publish "sec-scraper.csproj" -c Release -o /publish --no-restore

# RUNTIME STAGE
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /publish .

# Install Chromium for Puppeteer
RUN apt-get update && apt-get install -y \
    chromium \
    fonts-liberation \
    libnss3 \
    --no-install-recommends && \
    rm -rf /var/lib/apt/lists/*

ENV CHROME_PATH=/usr/bin/chromium
ENTRYPOINT ["dotnet", "sec-scraper.dll"]
