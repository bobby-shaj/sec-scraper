using Azure.Identity;
using Azure.Storage.Blobs;
using FocusDB.Repositories;
using FocusDB.Repositories.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using sec_scraper;

// 1. Setup the Host (but don't use RunAsync yet)
var builder = Host.CreateApplicationBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("FocusDBConnection")
    ?? throw new InvalidOperationException("Critical: FocusDBConnection is not found in configuration.");

var azureOptions = builder.Configuration.GetSection("AzureStorage").Get<AzureStorageOptions>();
var clientOptions = new BlobClientOptions
{
    Retry =
    {
        Delay = TimeSpan.FromSeconds(2),                // Wait 2s before first retry
        MaxRetries = 3,                                 // Try up to 3 times
        Mode = Azure.Core.RetryMode.Exponential,        // Wait longer each time
        NetworkTimeout = TimeSpan.FromSeconds(30),
    }
};

if (azureOptions != null)
{
    if (!string.IsNullOrWhiteSpace(azureOptions.ConnectionString))
    {
        builder.Services.AddSingleton(x =>
            new BlobServiceClient(azureOptions.ConnectionString, clientOptions));
        builder.Services.AddSingleton(x =>
        {
            var serviceClient = x.GetRequiredService<BlobServiceClient>();
            return serviceClient.GetBlobContainerClient("filing-documents");
        });
    }
    else
    {
        // Use the Service URI (https://accountname.blob.core.windows.net)
        // instead of a full Connection String with a key.
        var accountName = builder.Configuration["AzureStorage:AccountName"]!;
        var storageUri = new Uri($"https://{accountName}.blob.core.windows.net");

        builder.Services.AddSingleton(x => 
            new BlobServiceClient(storageUri, new DefaultAzureCredential()));
        builder.Services.AddSingleton(x =>
        {
            var serviceClient = x.GetRequiredService<BlobServiceClient>();
            return serviceClient.GetBlobContainerClient("filing-documents");
        });
    }
}

// 2. Register your existing services 
builder.Services.AddHttpClient();
builder.Services.AddScoped<IDbConnectionFactory, ScraperConnectionFactory>();
builder.Services.AddScoped<ITenantRepository, TenantRepository>();
builder.Services.AddScoped<IFilingRepository, FilingRepository>();
builder.Services.AddScoped<SecDataProcessingService>();

using IHost host = builder.Build();

// EXECUTE THE SCRAPER
try
{
    using (var mainScope = host.Services.CreateScope())
    {
        var processor = mainScope.ServiceProvider.GetRequiredService<SecDataProcessingService>();

        Console.WriteLine("Starting SEC Scraper Daily Run...");
        await processor.Execute();
        Console.WriteLine("Scraper Run Completed Successfully.");
    }
}
catch (Exception ex)
{
    // Resolve the logger from the host to log the fatal error
    var logger = host.Services.GetRequiredService<ILogger<Program>>();
    logger.LogCritical(ex, "The Scraper application terminated unexpectedly.");

    // Optional: Exit with a non-zero code so Azure/Monitoring knows it failed
    Environment.ExitCode = 1;
}
finally
{
    await host.StopAsync();
}