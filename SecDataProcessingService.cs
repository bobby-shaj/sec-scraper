using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FocusDB.Repositories.Interfaces;
using FocusLib.Models.DB;
using FocusLib.Models.SEC;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using PuppeteerSharp;
using PuppeteerSharp.Media;
using System;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sec_scraper
{
    public class SecDataProcessingService
    {
        private readonly string _secApiUrl = "https://data.sec.gov/submissions/";
        private readonly HttpClient _httpClient;
        private readonly ILogger<SecDataProcessingService> _logger;

        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly BlobContainerClient _containerClient;

        private readonly Dictionary<string, string> userAgentInfo = new Dictionary<string, string>()
        {
            ["User-Agent"] = "FocusUniversal (babak@focusuniversal.com)"
        };


        public SecDataProcessingService(HttpClient httpClient,
                                        ILogger<SecDataProcessingService> logger,
                                        BlobContainerClient containerClient,
                                        IServiceScopeFactory serviceScopeFactory)
        {
            _httpClient = httpClient;
            _logger = logger;
            _containerClient = containerClient;
            _serviceScopeFactory = serviceScopeFactory;
        }

        public async Task Execute()
        {
            _logger.LogInformation("Job executed at: {time}", DateTimeOffset.Now);

            // Perform the "Ensure Created" check once
            try
            {
                Console.WriteLine($"Verifying storage container: {_containerClient.Name}......");
                await _containerClient.CreateIfNotExistsAsync();
                Console.WriteLine("Storage container verified/created!!");
            }
            catch (Exception ex)
            {
                // If this fails, we catch it early before the scraper starts wasting SEC requests
                Console.WriteLine($"STORAGE INITIALIZATION ERROR: {ex.Message}");
                throw;
            }

            // 1. Get the list of all tenants.
            // We use a temporary scope here to fetch the "Master List"
            // while the ConnectionFactory has NO TenantId set.
            List<TenantLookupDto> tenants;
            using (var globalScope = _serviceScopeFactory.CreateScope())
            {
                var tenantRepo = globalScope.ServiceProvider.GetRequiredService<ITenantRepository>();
                tenants = (await tenantRepo.GetAllActiveTenantsAsync()).ToList();
            }

            _logger.LogInformation($"Found {tenants.Count} tenants to process.");

            // 2. Iterate through each tenant
            foreach (var tenant in tenants)
            {
                // CRITICAL: Create a NEW scope for this specific tenant
                using (var tenantScope = _serviceScopeFactory.CreateScope())
                {
                    try
                    {
                        // 3. Resolve the factory and SET the Identity
                        var factory = (ScraperConnectionFactory)tenantScope.ServiceProvider
                            .GetRequiredService<IDbConnectionFactory>();
                            
                        factory.CurrentTenantId = tenant.TenantId;

                        // 4. Resolve the Repositories
                        // Because they are Scoped, they will share the SAME factory instance
                        // we just updated above.
                        var filingRepo = tenantScope.ServiceProvider.GetRequiredService<IFilingRepository>();

                        _logger.LogInformation("Processing filings for: {TenantName} (CIK: {CIK}", tenant.DisplayName, tenant.Cik);


                        // 5. Run sequential tasks
                        //var cik = "0001590418";
                        //var cikNoLeadingZeros = GetNoLeadingZeroCIK(tenant.Cik);

                        // Fetch latest filing data from SEC API
                        var fetchedSecData = await GetSecFilingData(tenant.Cik);
                        int filingCountSEC = (int)fetchedSecData?.AccessionNumber?.Count!;

                        // Fetch filing count for company from Database
                        int filingCountDB = await filingRepo.GetFilingCount(tenant.Cik);

                        if (filingCountSEC > filingCountDB)
                        {
                            _logger.LogInformation("Babak, in conditional!!");

                            var browserFetcher = new BrowserFetcher();
                            var revisionInfo = await browserFetcher.DownloadAsync();

                            var launchOptions = new LaunchOptions
                            {
                                ExecutablePath = revisionInfo.GetExecutablePath(),
                                Headless = true,
                                Args = new[]
                                {
                                    "--no-sandbox", 
                                    "--disable-setuid-sandbox", 
                                    "--disable-dev-shm-usage"
                                }
                            };

                            using (var browser = await Puppeteer.LaunchAsync(launchOptions))
                            {
                                int newFilingCount = filingCountSEC - filingCountDB;
                                Console.WriteLine($"XXX --- count: {newFilingCount}, sec #: {filingCountSEC}, DB: {filingCountDB}");
                                var filingExhibitsList = await GetFilingExhibitData(tenant.Cik, fetchedSecData, newFilingCount, browser);
                                var ids = await InsertNewFilingsToDB(fetchedSecData!, filingExhibitsList, newFilingCount, tenant.Cik, filingRepo);
                                await CreateFilingPdfDocs(tenant.Cik, filingExhibitsList, fetchedSecData, browser);
                                await InsertFilingExhibitsToDB(ids, filingExhibitsList!, filingRepo);
                                await DownloadFiles(tenant.Cik, fetchedSecData, newFilingCount);
                                Console.WriteLine("Done!");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Catching here ensures one bad tenant doesn't stop the whole day's run
                        _logger.LogError(ex, "Error processing tenant {TenantName}", tenant.DisplayName);
                    }
                }  // Scope ends here: Connection is closed, and TenantId is wiped from memory
            }   
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="cik"></param>
        /// <param name="filing"></param>
        /// <param na   me="filingInfoDict"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        private async Task CreateFilingPdfDocs(string cik,
                                               List<List<FilingExhibit?>?> filingExhibitsList,
                                               SecFilingData secFilingData,
                                               IBrowser browser)
        {
            for (int i = 0; i < filingExhibitsList.Count; i++)
            {
                if (filingExhibitsList.ElementAt(i) == null) continue;
                using (var page = await browser.NewPageAsync())
                {
                    List<string> pdfDocuments = new List<string>();

                    await page.EvaluateExpressionOnNewDocumentAsync(@"
                            () => {
                                Object.defineProperty(navigator, 'webdriver', { get: () => false });
                            }
                        ");
                    await page.SetUserAgentAsync(userAgentInfo["User-Agent"]);

                    var accessionNum = secFilingData.AccessionNumber?[i];
                    List<byte[]> pdfArrayList = new List<byte[]>();

                    foreach (FilingExhibit? document in filingExhibitsList.ElementAt(i)!)
                    {
                        await page.GoToAsync(document?.Url, new NavigationOptions
                        {
                            WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
                            Timeout = 60000
                        });
                        byte[] pdfData = await page.PdfDataAsync(new PdfOptions
                        {
                            Format = PaperFormat.A4,
                            PrintBackground = true,
                        });
                        pdfArrayList.Add(pdfData);
                    }

                    // 1. Prepare the memory stream for the final merged PDF
                    using (var resultMs = new MemoryStream())
                    {
                        using (var resultPDF = new PdfDocument())
                        {
                            foreach (var pdf in pdfArrayList)
                            {
                                using (var src = new MemoryStream(pdf))
                                {
                                    using (var srcPDF = PdfReader.Open(src, PdfDocumentOpenMode.Import))
                                    {
                                        for (int j = 0; j < srcPDF.PageCount; j++)
                                        {
                                            resultPDF.AddPage(srcPDF.Pages[j]);
                                        }
                                    }
                                }
                            }

                            // 2. Save the PDF content into our result memory stream
                            resultPDF.Save(resultMs);
                        }

                        // 3. CRITICAL: Reset the stream position to the begining before uploading
                        resultMs.Position = 0;

                        // 4. Define the Azure Path (Virtual Directory structure)
                        // Note: No Directory.Exists check needed
                        string blobName = $"{cik}/{accessionNum}/file.pdf";
                        var blobClient = _containerClient.GetBlobClient(blobName);

                        // 5. Upload to Azure
                        await blobClient.UploadAsync(resultMs, new BlobUploadOptions
                        {
                            HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf"}
                        });

                        _logger.LogInformation("Merged PDF uploaded to: {BlobName}", blobName);
                    }
                        
                }
            }
        }
            

        /// <summary>       
        /// 
        /// </summary>
        /// <param name="cik"></param>
        /// <param name="filing"></param>
        /// <param name="filingInfoDict"></param>
        /// <param name="url"></param>
        /// <returns></returns>
        private async Task<List<List<FilingExhibit?>?>> GetFilingExhibitData(string cik, SecFilingData filing,
                                                                                   int newFilingCount, IBrowser browser)
        {
            List<List<FilingExhibit>?> resultList = new List<List<FilingExhibit>?>();

            // URL for company filing root folder
            string filesUrlBase = $"https://www.sec.gov/Archives/edgar/data/{cik}";

            // Construct url for each SEC filing accession index file `XXXXX-XX-XXXXX-index.html`
            // which contains info about a filing's files and url to each
            List<string> indexFilesUrlList = new List<string>();
            for (int i = 0; i < newFilingCount; i++)
            {
                var accessionNum = filing?.AccessionNumber?[i];
                string[] arr = accessionNum?.Split('-')!;
                if (arr != null && arr.Length == 3)
                {
                    string accessionNumFormated = arr[0] + arr[1] + arr[2];
                    indexFilesUrlList.Add($"{filesUrlBase}/{accessionNumFormated}/{accessionNum}-index.html");
                }
            }




            foreach (var pageUrl in indexFilesUrlList)
            {
                // SEC Rule: Max 10 requests per second. Adding a small delay 
                // helps keep the Azure IP from being "gray-listed"
                await Task.Delay(500);

                using (var page = await browser.NewPageAsync())
                {
                    try
                    {
                        List<FilingExhibit>? filesList = new List<FilingExhibit>();

                        await page.SetUserAgentAsync(userAgentInfo["User-Agent"]);
                        // Set a standart desktop resolution
                        await page.SetViewportAsync(new ViewPortOptions { Width = 1920, Height = 1080 });

                        var response = await page.GoToAsync(pageUrl, new NavigationOptions
                        {
                            WaitUntil = new[] { WaitUntilNavigation.Networkidle2 },
                            Timeout = 60000
                        });


                        var tableSelector = "table[summary='Document Format Files'], table.tableFile";

                        // Ensure the table actually exists
                        //try
                        //{
                        //    await page.WaitForSelectorAsync(tableSelector, new WaitForSelectorOptions { Timeout = 10000 });
                        //}
                        //catch (WaitTaskTimeoutException)
                        //{
                        //    // DIAGNOSTIC: If the table isn't found, log WHAT we are seeing instead.
                        //    var title = await page.GetTitleAsync();
                        //    var body = await page.GetContentAsync();
                        //    var snippet = body.Length > 300 ? body.Substring(0, 300) : body;

                        //    _logger.LogError("SEC Blocked/Different Layout at {Url}. Title: {Title}. Snippet: {Snippet}", pageUrl, title, snippet);

                        //    // Stop the entire run if we are clearly blocked
                        //    if (title.Contains("Request Rate") || title.Contains("Access Denied"))
                        //    {
                        //        throw new Exception("Scraper blocked by SEC Rate Limiting.");
                        //    }

                        //    throw; // Continue to the outer catch
                        //}

                        var title = await page.GetTitleAsync();
                        Console.WriteLine($"DEBUG: Response Status: {response?.Status}");
                        Console.WriteLine($"DEBUG: SEC Page Title: {title}");

                        var rows = await page.QuerySelectorAllAsync($"{tableSelector} tr");

                        // Start at 1 to skip header
                        for (int i = 2; i < rows.Length; i++)
                        {
                            var cols = await rows[i].QuerySelectorAllAsync("td");
                            if (cols.Length < 5) continue; // Safety check for empty/short rows

                            // We want first row (main file) and subsequent row(s) if attachments, i.e. EX-4.1, exist 
                            // otherwise, break and don't scrape any further 
                            var typeHandle = await cols[3].GetPropertyAsync("textContent");
                            var type = (await typeHandle.JsonValueAsync<string>())?.Trim() ?? "";

                            // Our business logic: Main file (i=1) or Exhibits (Ex-)
                            if (i > 2 && !type.StartsWith("EX-", StringComparison.OrdinalIgnoreCase)) break;

                            var dto = new FilingExhibit { Type = type };

                            var descHandle = await cols[1].GetPropertyAsync("textContent");
                            dto.Description = (await descHandle.JsonValueAsync<string>())?.Trim();

                            var anchorTag = await cols[2].QuerySelectorAsync("a");
                            if (anchorTag != null)
                            {
                                var titleHandle = await anchorTag.GetPropertyAsync("textContent");
                                dto.Title = (await titleHandle.JsonValueAsync<string>())?.Trim();

                                var hrefHandle = await anchorTag.GetPropertyAsync("href");
                                var aUrl = await hrefHandle.JsonValueAsync<string>();

                                dto.Url = aUrl.Replace(@"/ix?doc=", "");
                                dto.DisplayUrl = dto.Url;
                            }

                            var sizeHandle = await cols[4].GetPropertyAsync("textContent");
                            string sizeInText = (await sizeHandle.JsonValueAsync<string>())?.Trim() ?? "";
                            if (int.TryParse(sizeInText, out int sizeValue))
                            {
                                dto.Size = sizeValue;
                            }

                            filesList.Add(dto);
                        }
                        resultList.Add(filesList);
                    }
                    catch (Exception ex)
                    {
                        var title = await page.GetTitleAsync();
                        _logger.LogWarning("Failed URL: {Url}. SEC Page Title: {Title}", pageUrl, title);
                        // Crucial: Add an empty list or null to keep the ResultList count aligned with NewFilingCount
                        resultList.Add(null);
                    }
                }
            }
            resultList.Reverse();
            return resultList!;
        }

        private async Task<List<int>> InsertNewFilingsToDB(SecFilingData fetchedSecData, 
                                                           List<List<FilingExhibit?>?> filingExhibitsList, 
                                                           int rowsToAddCount,
                                                           string cik,
                                                           IFilingRepository filingRepository)
            {
            // Extract/create Filing class for each new SEC filing
            var newFilingsList = new List<Filing>();
            for (int i = 0; i < rowsToAddCount; i++)
            {
                Filing newFiling = new Filing()
                {
                    AccessionNum = fetchedSecData?.AccessionNumber?[i], 
                    FilingDate = fetchedSecData?.FilingDate?[i],
                    FilingType = fetchedSecData?.Form?[i],
                    Size = fetchedSecData?.Size?[i],
                    IsXBRL = fetchedSecData?.IsXBRL?[i],
                    IsInlineXBRL = fetchedSecData?.IsXBRL?[i],
                    PrimaryDocument = fetchedSecData?.PrimaryDocument?[i],
                    PrimaryDocDesc = fetchedSecData?.PrimaryDocDescription?[i],
                    PrimaryDocURL = filingExhibitsList[i]?[0]?.Url,
                    CIK = cik
                };
                newFilingsList.Add(newFiling);
            }
            return await filingRepository.InsertFilingsAndReturnIDs(newFilingsList);
        }

        private async Task InsertFilingExhibitsToDB(List<int> filingIDs, List<List<FilingExhibit>> filingExhibitsLists, IFilingRepository filingRepository)
        {

            await filingRepository.InsertFilingExhibitsToDB(filingIDs, filingExhibitsLists);
        }

        private async Task<SecFilingData> GetSecFilingData(string cik)
        {
            cik = "CIK" + cik;

            // First, check if CIK number's pattern is correct
            string pattern = @"^CIK\d{10}$";
            bool isMatch = Regex.IsMatch(cik, pattern);
            SecSubmission? secSubmissions = null;

            if (!isMatch)
            {
                throw new Exception();
            }

            string userAgent = @"babak@focusuniversal.com";
            var uri = _secApiUrl + $"{cik}.json";
            string jsonContent = "";
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);

            try
            {
                using (var response = await _httpClient.GetAsync(uri))
                {
                    response.EnsureSuccessStatusCode();
                    jsonContent = await response.Content.ReadAsStringAsync();
                }
            }
            catch (HttpRequestException ex)
            {
                // Handle potential exceptions (e.g., timeout, network issues)
                Console.WriteLine($"Error getting data: {ex.Message}");
                throw;
            }

            try
            {
                secSubmissions = JsonSerializer.Deserialize<SecSubmission>(jsonContent)!;
            }
            catch (JsonException ex)
            {
                Console.WriteLine("JSON exception: " + ex.Message);
                throw;
            }
            catch (SerializationException ex)
            {
                Console.WriteLine("Error deserializing JSON: " + ex.Message);
                throw;
            }

            return secSubmissions.Filings?.Recent!;
        }

        private async Task DownloadFiles(string cik, SecFilingData secFilingData, int newFilingCount)
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", @"FocusUniversal babak@focusuniversal.com");

            for (int i = 0; i < newFilingCount; i++)
            {
                if (secFilingData.IsXBRL?[i] == 0) continue;

                var accessionNum = secFilingData.AccessionNumber?[i];
                var accessionNumNoDashes = accessionNum?.Replace("-", "");

                var xlsxfileURL = $"https://www.sec.gov/Archives/edgar/data/{cik}/{accessionNumNoDashes}/Financial_Report.xlsx";
                // Define the virtual path in Azure 
                string blobNameXlsx = $"{cik}/{accessionNum}/Financial_Report.xlsx";
                var blobClientXlsx = _containerClient.GetBlobClient(blobNameXlsx);

                try
                {

                    // Use SendAsync instead of GetStreamAsync to prevent exception on 404
                    using var request = new HttpRequestMessage(HttpMethod.Get, xlsxfileURL);
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (response.IsSuccessStatusCode)
                    {
                        // Only if the file actually exists do we start the streaming process
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            // Upload directly from the HTTP stream to Azure Blob storage
                            await blobClientXlsx.UploadAsync(stream, new BlobUploadOptions
                            {
                                HttpHeaders = new BlobHttpHeaders
                                {
                                    ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                                }
                            });
                            _logger.LogInformation("Successfully streamed Excel report to Azure: {blobNameXlsx}", blobNameXlsx);
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // This is now a silent 'information' log rather than a 'fail' log
                        _logger.LogInformation("Filing {acc} has no Financial_Report.xlsx; skipping upload.", accessionNum);
                    }
                    else
                    {
                        // We still want to know if we get blocked (403) or the server is down (500)
                        _logger.LogWarning("Unexpected status {Code} when checking for Excel at {Url}", response.StatusCode, xlsxfileURL);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical failure attempting to reach SEC for Financial_Report.xlsx download.");
                }
                    
                var xbrlfileURL = $"https://www.sec.gov/Archives/edgar/data/{cik}/{accessionNumNoDashes}/{accessionNum}-xbrl.zip";
                string blobNameZip = $"{cik}/{accessionNum}/{accessionNum}-xbrl.zip";
                var blobClientZip = _containerClient.GetBlobClient(blobNameZip);

                try
                {

                    using var request = new HttpRequestMessage(HttpMethod.Get, xbrlfileURL);
                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (response.IsSuccessStatusCode)
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            // Upload directly from the source stream to Azure
                            await blobClientZip.UploadAsync(stream, new BlobUploadOptions
                            {
                                HttpHeaders = new BlobHttpHeaders
                                {
                                    // Standard MIME type for ZIP archives
                                    ContentType = "application/zip"
                                }
                            });
                            _logger.LogInformation("Successfully streamed ZIP archive to Azure: {blobNameZip}", blobNameZip);
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        _logger.LogInformation("Filing {acc} has no XBRL ZIP archive file.", accessionNum);
                    }
                    else
                    {
                        _logger.LogWarning("Unexpected status {Code} when checking for XBRL ZIP file at {Url}", response.StatusCode, xbrlfileURL);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Critical failure attempting to reach SEC for XBRL ZIP file.");
                }
            }
        }

        private string GetNoLeadingZeroCIK(string cik)
        {
            int ci = 0;
            while (ci < cik.Length && cik[ci] == '0')
            {
                ci++;
            }
            return cik.Substring(ci);
        }
    }
}
