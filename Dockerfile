# BUILD STAGE
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1. Copy the Scraper project file
COPY ["sec-scraper.csproj", "./"]

# 2. Use Wildcards to find the FocusDB project
# This looks in ANY casing for UserRegistration/Backend/FocusDB/
COPY ["UserRegistration/[Bb]ackend/[Ff]ocus[Dd][Bb]/*.csproj", "UserRegistration/Backend/FocusDB/"]

# 3. Restore
RUN dotnet restore "sec-scraper.csproj"

# 4. Copy everything else
COPY . .

# 5. Publish
RUN dotnet publish "sec-scraper.csproj" -c Release -o /app

# RUNTIME STAGE
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .

RUN apt-get update && apt-get install -y \
    chromium \
    fonts-liberation \
    libnss3 \
    --no-install-recommends && \
    rm -rf /var/lib/apt/lists/*

ENV CHROME_PATH=/usr/bin/chromium
ENTRYPOINT ["dotnet", "sec-scraper.dll"]
