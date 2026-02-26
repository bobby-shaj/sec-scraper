# BUILD STAGE
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1. Copy the Scraper project file
COPY ["sec-scraper.csproj", "./"]

# Copy the FocusDB project file using the path expected by your .csproj
# We use the 'UserRegistration' folder we created in Scraper repository GitHub Action
COPY ["UserRegistration/Backend/FocusDB/FocusDB.csproj", "UserRegistration/Backend/FocusDB/"]

# 2. Restore the scraper (this automatically restores the FocusDB dependency)
RUN dotnet restore "sec-scraper.csproj"

# 3. Copy everything else and publish
COPY . .

WORKDIR "/src/scraper-repo"
RUN dotnet publish "sec-scraper.csproj" -c Release -o /app

# RUNTIME STAGE
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app .

# Install ONLY the necessary Chromium libraries for Debian
RUN apt-get update && apt-get install -y \
	chromium \
	fonts-liberation \
	libnss3 \
	--no-install-recommends && \
	rm -rf /var/lib/apt/lists/*

# Set the path so your C# code knows where to look
ENV CHROME_PATH=/usr/bin/chromium
ENTRYPOINT ["dotnet", "sec-scraper.dll"]
