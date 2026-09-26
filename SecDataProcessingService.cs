using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FocusDB.Repositories.Interfaces;
using FocusLib.Models.DB;
using FocusLib.Models.SEC;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.Media;
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
        private readonly IDbConnectionFactory _dbConnectionFactory;
        private readonly IFilingRepository _filingRepository;

        private readonly Dictionary<string, string> userAgentInfo = new Dictionary<string, string>()
        {
            ["User-Agent"] = "FocusUniversal (babak@focusuniversal.com)"
        };
        private readonly string _cik = string.Empty;
        private readonly string _companyName = string.Empty;

        public SecDataProcessingService(HttpClient httpClient,
                                        ILogger<SecDataProcessingService> logger,
                                        BlobContainerClient containerClient,
                                        IDbConnectionFactory dbConnectionFactory,
                                        IFilingRepository filingRepository,
                                        IConfiguration configuration,
                                        IServiceScopeFactory serviceScopeFactory)
        {
            _httpClient = httpClient;
            _logger = logger;
            _containerClient = containerClient;
            _serviceScopeFactory = serviceScopeFactory;
            _dbConnectionFactory = dbConnectionFactory;
            _filingRepository = filingRepository;
            _cik = configuration["CompanySettings:cik"] ?? string.Empty;
            _companyName = configuration["CompanySettings:CompanyName"] ?? string.Empty;
        }

        public async Task Execute()
        {
            _logger.LogInformation("Job executed at: {time}", DateTimeOffset.Now);

            // Perform the "Ensure Created" check once
            try
            {
                Console.WriteLine($"Verifying storage container: {_containerClient.Name}......");
                await _containerClient.CreateIfNotExistsAsync();
                Console.WriteLine("Storage container verified/created");
            }
            catch (Exception ex)
            {
                // If this fails, we catch it early before the scraper starts wasting SEC requests
                Console.WriteLine($"STORAGE INITIALIZATION ERROR: {ex.Message}");
                throw;
            }


            try
            {
                var connection = _dbConnectionFactory.CreateConnectionAsync();

                // Fetch latest filing data from SEC API
                var fetchedSecData = await GetSecFilingData(_cik);
                int filingCountSEC = (int)fetchedSecData?.AccessionNumber?.Count!;

                // Fetch filing count for company from Database
                int filingCountDB = await _filingRepository.GetFilingCount(_cik);
                var delta = filingCountSEC - filingCountDB;

                delta = _cik.Equals("0001590418") ? delta = 5 : delta = 14;

                if (delta > 0)
                {
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
                        Console.WriteLine($"XXX --- count: {delta}, sec #: {filingCountSEC}, DB: {filingCountDB}");
                        var filingExhibitsList = await GetFilingExhibitData(_cik, fetchedSecData, delta, browser);
                        var ids = await InsertNewFilingsToDB(fetchedSecData!, filingExhibitsList, delta, _cik, _filingRepository);
                        await CreateFilingPdfDocs(_cik, filingExhibitsList, fetchedSecData, browser);
                        await DownloadFiles(_cik, fetchedSecData, delta);
                        await InsertFilingExhibitsToDB(ids, filingExhibitsList!, _filingRepository);
                        Console.WriteLine("Done!");
                    }
                }
            }
            catch (Exception ex)
            {
                // Catching here ensures one bad tenant doesn't stop the whole day's run
                _logger.LogError(ex, "Error processing company data {_companyName}", _companyName);
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
                var currentFilingExhibits = filingExhibitsList.ElementAtOrDefault(i);
                if (currentFilingExhibits == null) continue;

                var accessionNum = secFilingData.AccessionNumber?[i];

                try
                {
                    // We use one page per Filing to save memory overhead
                    using (var page = await browser.NewPageAsync())
                    {
                        // Stealth & Identity
                        await page.EvaluateExpressionOnNewDocumentAsync("() => { Object.defineProperty(navigator, 'webdriver', { get: () => false }); }");
                        await page.SetUserAgentAsync(userAgentInfo["User-Agent"]);

                        List<byte[]> pdfArrayList = new List<byte[]>();

                        foreach (FilingExhibit? document in currentFilingExhibits)
                        {
                            if (string.IsNullOrEmpty(document?.Url)) continue;

                            // --- Optimization: SEC Rate Limit Delay ---
                            await Task.Delay(500);

                            // --- Optimization: Robust Navigation with Retry ---
                            byte[]? pdfData = await NavigateAndCapturePdf(page, document.Url);

                            if (pdfData != null)
                            {
                                pdfArrayList.Add(pdfData);
                            }
                        }

                        if (pdfArrayList.Count == 0) continue;

                        // --- PDF Merging Logic ---
                        using (var resultMs = new MemoryStream())
                        {
                            using (var resultPDF = new PdfSharpCore.Pdf.PdfDocument())
                            {
                                foreach (var pdfBytes in pdfArrayList)
                                {
                                    using (var src = new MemoryStream(pdfBytes))
                                    using (var srcPDF = PdfSharpCore.Pdf.IO.PdfReader.Open(src, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import))
                                    {
                                        for (int j = 0; j < srcPDF.PageCount; j++)
                                        {
                                            resultPDF.AddPage(srcPDF.Pages[j]);
                                        }
                                    }
                                }
                                resultPDF.Save(resultMs);
                            }

                            resultMs.Position = 0;

                            // --- Azure Storage Upload ---
                            string blobName = $"filing-documents/{accessionNum}/file.pdf";
                            var blobClient = _containerClient.GetBlobClient(blobName);

                            await blobClient.UploadAsync(resultMs, new BlobUploadOptions
                            {
                                HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" }
                            });

                            _logger.LogInformation("✅ Successfully uploaded merged PDF: {BlobName}", blobName);
                         }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ Critical failure processing Accession: {Accession}", accessionNum);
                    // We continue so one bad filing doesn't stop the whole job
                    continue;
                }
            }
        }

        private async Task<byte[]?> NavigateAndCapturePdf(IPage page, string url, int maxRetries = 3)
        {
            int attempt = 0;
            while (attempt < maxRetries)
            {
                try
                {
                    attempt++;

                    // Optimization: DOMContentLoaded is faster/more reliable for SEC HTML
                    await page.GoToAsync(url, new NavigationOptions
                    {
                        WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                        Timeout = 60000
                    });

                    // Capture PDF
                    return await page.PdfDataAsync(new PdfOptions
                    {
                        Format = PaperFormat.A4,
                        PrintBackground = true,
                    });
                }
                catch (Exception ex) when (attempt < maxRetries)
                {
                    int delay = attempt * 2000; // Exponential backoff: 2s, 4s...
                    _logger.LogWarning("⚠️ Timeout/Error at {Url}. Retry {Attempt}/{Max}. Waiting {Delay}ms...", url, attempt, maxRetries, delay);
                    await Task.Delay(delay);
                }
            }
            return null; // Return null if all retries fail
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
                await Task.Delay(2000);

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
            //resultList.Reverse();
            return resultList!;
        }

        private async Task<List<int>> InsertNewFilingsToDB(SecFilingData fetchedSecData,
                                                           List<List<FilingExhibit?>?> filingExhibitsList,
                                                           int rowsToAddCount,
                                                           string cik,
                                                           IFilingRepository filingRepository)
        {
            // Extract/create Filing class for each new SEC filing
            List<Filing> newFilingsList = new List<Filing>();
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
                    PrimaryDocDesc = fetchedSecData?.PrimaryDocDesc?[i],
                    PrimaryDocURL = filingExhibitsList[i]?[0]?.Url,
                    CIK = cik
                };
                newFilingsList.Add(newFiling);
            }
            newFilingsList.Reverse();
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


            var uri = _secApiUrl + $"{cik}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgentInfo["User-Agent"]);
            //request.Headers.Add("User-Agent", userAgentInfo["User-Agent"]);
            string jsonContent = "";

            try
            {
                using (var response = await _httpClient.SendAsync(request))
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
            for (int i = 0; i < newFilingCount; i++)
            {
                if (secFilingData.IsXBRL?[i] == 0) continue;

                var accessionNum = secFilingData.AccessionNumber?[i];
                var accessionNumNoDashes = accessionNum?.Replace("-", "");

                var xlsxfileURL = $"https://www.sec.gov/Archives/edgar/data/{cik}/{accessionNumNoDashes}/Financial_Report.xlsx";
                // Define the virtual path in Azure 
                string blobNameXlsx = $"filing-documents/{accessionNum}/Financial_Report.xlsx";
                var blobClientXlsx = _containerClient.GetBlobClient(blobNameXlsx);

                try
                {

                    // Use SendAsync instead of GetStreamAsync to prevent exception on 404
                    using var request = new HttpRequestMessage(HttpMethod.Get, xlsxfileURL);
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgentInfo["User-Agent"]);
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
                string blobNameZip = $"filing-documents/{accessionNum}/{accessionNum}-xbrl.zip";
                var blobClientZip = _containerClient.GetBlobClient(blobNameZip);

                try
                {

                    using var request = new HttpRequestMessage(HttpMethod.Get, xbrlfileURL);
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgentInfo["User-Agent"]);
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
    }
}
