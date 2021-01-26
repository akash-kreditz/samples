﻿//////////////////////////////////////////////////////////////////////
//
// Open Payments Europe AB 2021
//
// Open Banking Platform - Payment Initiation Service
// 
// Payment Initiation example application
//
//////////////////////////////////////////////////////////////////////

using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Security.Cryptography.X509Certificates;
using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Configuration;
using Microsoft.CSharp.RuntimeBinder;
using Newtonsoft.Json;
using QRCoder;

namespace PaymentInitiation
{
    class Program
    {
        private const string QRCodeImageFilename = "QRCode.png";

        //
        // Configuration settings
        //
        public class Settings
        {
            public string ClientId { get; set; }
            public string RedirectURI { get; set; }
            public bool UseProductionEnvironment { get; set; }
            public string ProductionClientCertificateFile { get; set; }
            public string PSUIPAddress { get; set; }
            public string PSUUserAgent { get; set; }
        }

        //
        // Payment context
        //
        public class Payment
        {
            public string BicFi { get; }
            public string PaymentService { get; }
            public string PaymentProduct { get; }
            public string PaymentBody { get; }
            public string PaymentId { get; set; }
            public string PaymentAuthId { get; set; }
            public SCAMethod ScaMethod { get; set; }
            public string ScaData { get; set; }

            public Payment(string bicFi, string paymentService, string paymentProduct, string paymentBody)
            {
                this.BicFi = bicFi;
                this.PaymentService = paymentService;
                this.PaymentProduct = paymentProduct;
                this.PaymentBody = paymentBody;
            }
        }

        public enum SCAMethod
        {
            UNDEFINED = 1,
            OAUTH_REDIRECT,
            REDIRECT,
            DECOUPLED
        }

        private static String _authUri;
        private static String _apiUri;
        private static HttpClientHandler _apiClientHandler;
        private static string _paymentinitiationScope;
        private static string _psuIPAddress;
        private static string _psuUserAgent;
        private static string _psuCorporateId;
        private static string _clientId;
        private static string _clientSecret;
        private static string _redirectUri;
        private static string _token;
        private static Payment _payment;

        static async Task Main(string[] args)
        {
            if (args.Length != 1)
            {
                Usage();
                return;
            }
            string paymentName = args[0];

            Init(paymentName);

            //
            // Get an API access token from auth server with the scope needed
            //
            _token = await GetToken(_clientId, _clientSecret, _paymentinitiationScope);
            Console.WriteLine($"token: {_token}");
            Console.WriteLine();

            //
            // Create the payment
            //
            _payment.PaymentId = await CreatePaymentInitiation(_payment.BicFi, _payment.PaymentService, _payment.PaymentProduct, _payment.PaymentBody);
            Console.WriteLine($"paymentId: {_payment.PaymentId}");
            Console.WriteLine();

            //
            // Create a payment authorization object to be used for authorizing the payment with the end user
            //
            _payment.PaymentAuthId = await StartPaymentInitiationAuthorisationProcess(_payment.BicFi, _payment.PaymentService, _payment.PaymentProduct, _payment.PaymentId);
            Console.WriteLine($"authId: {_payment.PaymentAuthId}");
            Console.WriteLine();

            //
            // Start the payment authorization process with the end user
            //
            (_payment.ScaMethod, _payment.ScaData) = await UpdatePSUDataForPaymentInitiation(_payment.BicFi, _payment.PaymentService, _payment.PaymentProduct, _payment.PaymentId, _payment.PaymentAuthId);
            Console.WriteLine($"scaMethod: {_payment.ScaMethod}");
            Console.WriteLine($"data: {_payment.ScaData}");
            Console.WriteLine();

            bool scaSuccess;
            if (_payment.ScaMethod == SCAMethod.OAUTH_REDIRECT || _payment.ScaMethod == SCAMethod.REDIRECT)
            {
                //
                // Bank uses a redirect flow for Strong Customer Authentication
                //
                scaSuccess = await SCAFlowRedirect(_payment, "MyState");
            }
            else if (_payment.ScaMethod == SCAMethod.DECOUPLED)
            {
                //
                // Bank uses a decoupled flow for Strong Customer Authentication
                //
                scaSuccess = await SCAFlowDecoupled(_payment);
            }
            else
            {
                throw new Exception($"ERROR: unknown SCA method {_payment.ScaMethod}");
            }

            if (!scaSuccess)
            {
                Console.WriteLine("SCA failed");
                Console.WriteLine();
                return;
            }

            Console.WriteLine("SCA completed successfully");
            Console.WriteLine();

            //
            // Check the status of the payment, for this example until it changes from the initial
            // "RCVD" status to anything else
            //
            string transactionStatus = await GetPaymentInitiationStatus(_payment.BicFi, _payment.PaymentService, _payment.PaymentProduct, _payment.PaymentId);
            Console.WriteLine($"transactionStatus: {transactionStatus}");
            Console.WriteLine();
            while (transactionStatus.Equals("RCVD"))
            {
                await Task.Delay(2000);
                transactionStatus = await GetPaymentInitiationStatus(_payment.BicFi, _payment.PaymentService, _payment.PaymentProduct, _payment.PaymentId);
                Console.WriteLine($"transactionStatus: {transactionStatus}");
                Console.WriteLine();
            }
        }

        static void Usage()
        {
            Console.WriteLine("Usage: PaymentInitiation <payment name>");
        }

        static void Init(string paymentName)
        {
            //
            // Read configuration
            //
            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddJsonFile("appsettings.json", false, false);
            IConfigurationRoot config = configurationBuilder.Build();
            var settings = config.Get<Settings>();

            _clientId = settings.ClientId;
            _redirectUri = settings.RedirectURI;
            _psuIPAddress = settings.PSUIPAddress;
            _psuUserAgent = settings.PSUUserAgent;

            //
            // Read payment configuration and pick the chosen payment to process 
            //
            var jsonString = File.ReadAllText("payments.json");
            dynamic payments = JsonConvert.DeserializeObject<dynamic>(jsonString);
            foreach (var item in payments)
            {
                string name = item.Name;
                if (name.Equals(paymentName, StringComparison.OrdinalIgnoreCase))
                {
                    _payment = new Payment((string)item.BICFI,
                                           (string)item.PaymentService,
                                           (string)item.PaymentProduct,
                                           JsonConvert.SerializeObject(item.Payment, Newtonsoft.Json.Formatting.None));
                    _paymentinitiationScope = $"{item.PSUContextScope} paymentinitiation";
                    _psuCorporateId = item.PSUContextScope.Equals("corporate") ? item.PSUContextScope : null;
                    break;
                }
            }
            if (_payment.PaymentBody == null)
            {
                throw new Exception($"ERROR: payment {paymentName} not found");
            }

            //
            // Prompt for client secret
            //
            Console.Write("Enter your Client Secret: ");
            _clientSecret = ConsoleReadPassword();
            Console.WriteLine();

            _apiClientHandler = new HttpClientHandler();

            //
            // Set up for different environments
            //
            if (settings.UseProductionEnvironment)
            {
                Console.WriteLine("Using production");
                Console.WriteLine();
                _authUri = "https://auth.openbankingplatform.com";
                _apiUri = "https://api.openbankingplatform.com";

                Console.Write("Enter Certificate Password: ");
                string certPassword = ConsoleReadPassword();
                Console.WriteLine();

                X509Certificate2 certificate = new X509Certificate2(settings.ProductionClientCertificateFile, certPassword);
                _apiClientHandler.ClientCertificates.Add(certificate);
            }
            else
            {
                Console.WriteLine("Using sandbox");
                Console.WriteLine();
                _authUri = "https://auth.sandbox.openbankingplatform.com";
                _apiUri = "https://api.sandbox.openbankingplatform.com";
            }

        }

        private static string ConsoleReadPassword()
        {
            var password = "";
            ConsoleKeyInfo ch = Console.ReadKey(true);
            while (ch.Key != ConsoleKey.Enter)
            {
                password += ch.KeyChar;
                Console.Write('*');
                ch = Console.ReadKey(true);
            }
            return password;
        }

        private static void OpenBrowser(string url)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }

        private static string FormatBankIdURL(string autostartToken, string redirectUri)
        {
            return $"bankid:///?autostarttoken={autostartToken}&redirect={redirectUri}";
        }

        //
        // Generates a QR-code image from a character string and opens it with default application
        //
        private static void DisplayQRCode(string url)
        {
            QRCodeGenerator qrGenerator = new QRCodeGenerator();
            QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
            QRCode qrCode = new QRCode(qrCodeData);
            Bitmap qrCodeImage = qrCode.GetGraphic(20);
            qrCodeImage.Save(QRCodeImageFilename, ImageFormat.Png);
            string qrCodeUrl = "file://" + Path.GetFullPath(".") + "/" + QRCodeImageFilename;
            OpenBrowser(qrCodeUrl);
        }

        //
        // Will poll the SCA status indefinitely until status is either "finalised" or "failed"
        //
        private static async Task<bool> PollSCAStatus(Payment payment, int millisecondsDelay)
        {
            string scaStatus = await GetPaymentInitiationAuthorisationSCAStatus(payment.BicFi, payment.PaymentService, payment.PaymentProduct, payment.PaymentId, payment.PaymentAuthId);
            Console.WriteLine($"scaStatus: {scaStatus}");
            Console.WriteLine();
            while (!scaStatus.Equals("finalised") && !scaStatus.Equals("failed"))
            {
                await Task.Delay(millisecondsDelay);
                scaStatus = await GetPaymentInitiationAuthorisationSCAStatus(payment.BicFi, payment.PaymentService, payment.PaymentProduct, payment.PaymentId, payment.PaymentAuthId);
                Console.WriteLine($"scaStatus: {scaStatus}");
                Console.WriteLine();
            }
            if (scaStatus.Equals("failed"))
                return false;

            return true;
        }

        //
        // Starts a redirect flow for SCA by opening SCA URL in default browser (for end user to authenticate),
        // then prompts for authorisation code returned in final redirect query parameter "code".
        // (prompting for this is because of the simplicity of this example application that is not implementing a http server)
        //
        private static async Task<bool> SCAFlowRedirect(Payment payment, string state)
        {
            //
            // Fill in the details on the given redirect URL template
            //
            string url = payment.ScaData.Replace("[CLIENT_ID]", _clientId).Replace("[TPP_REDIRECT_URI]", WebUtility.UrlEncode(_redirectUri)).Replace("[TPP_STATE]", WebUtility.UrlEncode(state));
            Console.WriteLine($"URL: {url}");
            Console.WriteLine();

            OpenBrowser(url);

            //
            // If flow is OAuthRedirect, authorisation code needs to be activated
            //
            if (payment.ScaMethod == SCAMethod.OAUTH_REDIRECT)
            {
                Console.Write("Enter authorisation code returned by redirect query param: ");
                string authCode = Console.ReadLine();
                Console.WriteLine();

                string newToken = await ActivateOAuthPaymentAuthorisation(_authUri, payment.PaymentId, payment.PaymentAuthId, _clientId, _clientSecret, _redirectUri, _paymentinitiationScope, authCode);
                Console.WriteLine();
                if (String.IsNullOrEmpty(newToken))
                    return false;
            }

            //
            // Wait for a final SCA status
            //
            return await PollSCAStatus(payment, 2000);
        }

        //
        // Handles a decoupled flow by formatting a BankId URL, presenting it as an QR-code to be scanned
        // with BankId, then polling for a final SCA status of the authentication/auhorisation
        //
        private static async Task<bool> SCAFlowDecoupled(Payment payment)
        {
            string bankIdUrl = FormatBankIdURL(payment.ScaData, WebUtility.UrlEncode("https://openpayments.io"));
            DisplayQRCode(bankIdUrl);

            return await PollSCAStatus(payment, 2000);
        }

        //
        // Create a http client with the basic common attributes set for a request to auth server
        //
        private static HttpClient CreateGenericAuthClient()
        {
            var authClient = new HttpClient();
            authClient.BaseAddress = new Uri(_authUri);
            authClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            return authClient;
        }

        //
        // Create a http client with the basic common attributes set for a request to API:s
        //
        private static HttpClient CreateGenericApiClient(string bicFi)
        {
            var apiClient = new HttpClient(_apiClientHandler);
            apiClient.BaseAddress = new Uri(_apiUri);
            apiClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            apiClient.DefaultRequestHeaders.Add("X-BicFi", bicFi);
            apiClient.DefaultRequestHeaders.Add("PSU-IP-Address", _psuIPAddress);
            if (!String.IsNullOrEmpty(_psuCorporateId))
                apiClient.DefaultRequestHeaders.Add("PSU-Corporate-Id", _psuCorporateId);

            return apiClient;
        }

        private static async Task<String> GetToken(string clientId, string clientSecret, string scope)
        {
            Console.WriteLine("Get Token");
            var authClient = CreateGenericAuthClient();

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", scope)
            });

            var response = await authClient.PostAsync("/connect/token", content);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"statusCode: {(int)response.StatusCode}");
            Console.WriteLine($"responseBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.access_token;
        }

        private static async Task<string> ActivateOAuthPaymentAuthorisation(string authUri, string paymentId, string authId, string clientId, string clientSecret, string redirectUri, string scope, string authCode)
        {
            Console.WriteLine("Activate OAuth Payment Authorisation");
            var authClient = CreateGenericAuthClient();
            authClient.DefaultRequestHeaders.Add("X-PaymentId", paymentId);
            authClient.DefaultRequestHeaders.Add("X-PaymentAuthorisationId", authId);

            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("redirect_uri", redirectUri),
                new KeyValuePair<string, string>("scope", scope),
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", authCode)
            });

            var response = await authClient.PostAsync("/connect/token", content);
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.access_token;
        }

        private static async Task<String> CreatePaymentInitiation(string bicFi, string paymentService, string paymentProduct, string jsonPaymentBody)
        {
            Console.WriteLine("Create Payment Initiation");
            var apiClient = CreateGenericApiClient(bicFi);
            apiClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
            apiClient.DefaultRequestHeaders.Add("PSU-User-Agent", _psuUserAgent);

            Console.WriteLine($"requestBody: {jsonPaymentBody}");
            var response = await apiClient.PostAsync($"/psd2/paymentinitiation/v1/{paymentService}/{paymentProduct}", new StringContent(jsonPaymentBody, Encoding.UTF8, "application/json"));
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.paymentId;
        }

        private static async Task<String> StartPaymentInitiationAuthorisationProcess(string bicFi, string paymentService, string paymentProduct, string paymentId)
        {
            Console.WriteLine("Start Payment Initiation Authorisation Process");
            var apiClient = CreateGenericApiClient(bicFi);
            apiClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());

            string jsonBody = "";
            var response = await apiClient.PostAsync($"/psd2/paymentinitiation/v1/{paymentService}/{paymentProduct}/{paymentId}/authorisations", new StringContent(jsonBody, Encoding.UTF8, "application/json"));
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.authorisationId;
        }

        private static async Task<(SCAMethod, string)> UpdatePSUDataForPaymentInitiation(string bicFi, string paymentService, string paymentProduct, string paymentId, string authId)
        {
            Console.WriteLine("Update PSU Data For Payment Initiation");
            var apiClient = CreateGenericApiClient(bicFi);
            apiClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());

            string jsonBody = "{\"authenticationMethodId\": \"mbid\"}";
            var response = await apiClient.PutAsync($"/psd2/paymentinitiation/v1/{paymentService}/{paymentProduct}/{paymentId}/authorisations/{authId}", new StringContent(jsonBody, Encoding.UTF8, "application/json"));
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            string data = "";
            IEnumerable<string> headerValues = response.Headers.GetValues("aspsp-sca-approach");
            string scaApproach = headerValues.FirstOrDefault();
            SCAMethod method = SCAMethod.UNDEFINED;
            if (scaApproach.Equals("REDIRECT"))
            {
                try
                {
                    data = responseBody._links.scaOAuth.href;
                    method = SCAMethod.OAUTH_REDIRECT;
                }
                catch (RuntimeBinderException)
                {
                    try
                    {
                        data = responseBody._links.scaRedirect.href;
                        method = SCAMethod.REDIRECT;
                    }
                    catch (RuntimeBinderException)
                    {
                    }
                }
            }
            else if (scaApproach.Equals("DECOUPLED"))
            {
                data = responseBody.challengeData.data[0];
                method = SCAMethod.DECOUPLED;
            }

            return (method, data);
        }

        private static async Task<String> GetPaymentInitiationAuthorisationSCAStatus(string bicFi, string paymentService, string paymentProduct, string paymentId, string authId)
        {
            Console.WriteLine("Get Payment Initiation Authorisation SCA Status");
            var apiClient = CreateGenericApiClient(bicFi);
            apiClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());

            var response = await apiClient.GetAsync($"/psd2/paymentinitiation/v1/{paymentService}/{paymentProduct}/{paymentId}/authorisations/{authId}");
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.scaStatus;
        }

        private static async Task<String> GetPaymentInitiationStatus(string bicFi, string paymentService, string paymentProduct, string paymentId)
        {
            Console.WriteLine("Get Payment Initiation Status");
            var apiClient = CreateGenericApiClient(bicFi);
            apiClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());

            var response = await apiClient.GetAsync($"/psd2/paymentinitiation/v1/{paymentService}/{paymentProduct}/{paymentId}/status");
            string responseContent = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"ERROR: statusCode={(int)response.StatusCode} Message={responseContent}");
            }
            Console.WriteLine($"resultStatusCode: {(int)response.StatusCode}");
            Console.WriteLine($"resultBody: {responseContent}");
            Console.WriteLine();

            dynamic responseBody = JsonConvert.DeserializeObject<dynamic>(responseContent);

            return responseBody.transactionStatus;
        }
    }
}
