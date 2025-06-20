using FIXLinkTradingServer.Models;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.OpenSsl;
using System.Text;

namespace FIXLinkTradingServer.Services
{
    public class MarketExecutionResult
    {
        public decimal ExecutedQuantity { get; set; }
        public decimal ExecutedValue { get; set; }
        public decimal ExecutedPrice { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public interface IFIXLinkService
    {
        Task<bool> ConnectAsync();
        Task<MarketExecutionResult> SendTradeToMarketAsync(string symbol, decimal quantity, string side);
        Task<MarketExecutionResult> SendDollarTradeToMarketAsync(string symbol, decimal dollarAmount, string side);
        Task DisconnectAsync();
        bool IsConnected { get; }
    }

    public class FIXLinkService : IFIXLinkService
    {
        private readonly ILogger<FIXLinkService> _logger;
        private readonly IConfiguration _configuration;
        private HubConnection _hubConnection;
        private CookieContainer _cookies;
        private List<string> _sessionNames = new List<string>();
        private long _sendSeq = 0;
        private readonly string _baseUrl;
        private readonly string _publicKeyPath;
        private readonly string _secret;
        private readonly string _identity;

        public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

        public FIXLinkService(ILogger<FIXLinkService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _baseUrl = _configuration["FIXLink:BaseUrl"] ?? "http://nypralsqlvm01.sscnydirect.local/FIXAPIDev1/";
            _publicKeyPath = _configuration["FIXLink:PublicKeyPath"] ?? "C:\\tmp\\publickey.pem";
            _secret = _configuration["FIXLink:Secret"] ?? "principal mongo cleardynamo";
            _identity = _configuration["FIXLink:Identity"] ?? "user01@Acme";
        }

        public async Task<bool> ConnectAsync()
        {
            try
            {
                // Step 1: Authenticate
                _cookies = await AuthenticateAsync();
                if (_cookies == null)
                {
                    _logger.LogError("Authentication failed");
                    return false;
                }

                // Step 2: Connect and Handshake
                await ConnectAndHandshakeAsync();
                
                _logger.LogInformation("Successfully connected to FIXLink API");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to FIXLink API");
                return false;
            }
        }

        private async Task<CookieContainer> AuthenticateAsync()
        {
            var authenticateMsg = new JsonObject
            {
                ["timestamp"] = DateTime.Now.ToString("s"),
                ["secret"] = _secret
            };
            
            var jsonString = authenticateMsg.ToJsonString();
            var encryptedString = EncryptString(_publicKeyPath, jsonString);
            
            var cookies = new CookieContainer();
            using var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
            
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{_baseUrl}api/v1/Authentication/login"));
            request.Headers.TryAddWithoutValidation("Identity", _identity);
            request.Headers.TryAddWithoutValidation("Authorization", encryptedString);
            
            var response = await client.SendAsync(request);
            
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation($"Authentication successful: {response.StatusCode}");
                return cookies;
            }
            
            _logger.LogError($"Authentication failed: {response.StatusCode}");
            return null;
        }

        private async Task ConnectAndHandshakeAsync()
        {
            var url = _baseUrl + "FIXHub";
            
            _hubConnection = new HubConnectionBuilder()
                .WithUrl(url, options => { options.Cookies = _cookies; })
                .WithAutomaticReconnect()
                .Build();

            _hubConnection.Closed += async (exception) =>
            {
                _logger.LogWarning($"Connection closed: {exception?.Message}");
            };

            _hubConnection.On<string>("ReceiveMessage", (message) =>
            {
                _logger.LogInformation($"Received FIX Message: {message}");
                ProcessReceivedMessage(message);
            });

            _hubConnection.On<string>("ReceiveStatus", (message) =>
            {
                _logger.LogInformation($"Received Status: {message}");
            });

            await _hubConnection.StartAsync();

            // Perform handshake
            var handshakeMsg = new JsonObject
            {
                ["lastRecvSeq"] = -1,
                ["resetSendSeq"] = true
            };

            var result = await _hubConnection.InvokeAsync<string>("Handshake", handshakeMsg.ToJsonString());
            _logger.LogInformation($"Handshake Result: {result}");

            // Parse handshake result to get session names
            var jsonResult = JsonNode.Parse(result);
            var sessionNamesArray = jsonResult?["sessionNames"];
            if (sessionNamesArray != null)
            {
                foreach (var sessionName in sessionNamesArray.AsArray())
                {
                    if (sessionName != null)
                        _sessionNames.Add(sessionName.ToString());
                }
            }
        }

        public async Task<MarketExecutionResult> SendTradeToMarketAsync(string symbol, decimal quantity, string side)
        {
            if (!IsConnected)
            {
                throw new InvalidOperationException("Not connected to FIXLink API");
            }

            // Create FIX order message
            var order = new FIXMessage();
            order.SetField(8, "FIX.4.2"); // BeginString
            order.SetField(35, "D"); // MsgType - New Order
            order.SetField(55, symbol); // Symbol
            order.SetField(54, side); // Side (1=Buy, 2=Sell)
            order.SetField(38, quantity.ToString()); // OrderQty
            order.SetField(40, "1"); // OrdType - Market Order
            order.SetField(59, "0"); // TimeInForce - Day
            order.SetField(21, "3"); // HandlInst - Manual Order Best Execution
            order.SetField(11, Guid.NewGuid().ToString()); // ClOrdID
            order.SetField(60, DateTime.Now.ToString("yyyyMMdd-HH:mm:ss")); // TransactTime

            var fixString = order.ToFIXString();
            
            _sendSeq++;
            var sendMsg = new JsonObject
            {
                ["session"] = _sessionNames.FirstOrDefault() ?? "SESSION01",
                ["msg"] = fixString,
                ["sendSeq"] = _sendSeq
            };

            var result = await _hubConnection.InvokeAsync<string>("SendMessage", sendMsg.ToJsonString());
            _logger.LogInformation($"Sent trade to market: {result}");

            // Simulate market execution (in real implementation, this would come from ReceiveMessage)
            return new MarketExecutionResult
            {
                ExecutedQuantity = quantity,
                ExecutedValue = quantity * 65.5m, // Simulated price
                ExecutedPrice = 65.5m,
                Success = true,
                Message = "Executed at market"
            };
        }

        public async Task<MarketExecutionResult> SendDollarTradeToMarketAsync(string symbol, decimal dollarAmount, string side)
        {
            // Similar to SendTradeToMarketAsync but for dollar-based orders
            // Implementation would be similar but using dollar amount instead of quantity
            
            // Simulate market execution
            var simulatedPrice = 65.5m;
            var executedQuantity = Math.Floor(dollarAmount / simulatedPrice);
            var executedValue = executedQuantity * simulatedPrice;

            return new MarketExecutionResult
            {
                ExecutedQuantity = executedQuantity,
                ExecutedValue = executedValue,
                ExecutedPrice = simulatedPrice,
                Success = true,
                Message = "Executed at market"
            };
        }

        private void ProcessReceivedMessage(string message)
        {
            try
            {
                var jsonMsg = JsonNode.Parse(message);
                var fixMsg = jsonMsg?["msg"]?.ToString();
                
                if (!string.IsNullOrEmpty(fixMsg))
                {
                    var parsedFix = FIXMessage.ParseFIXString(fixMsg);
                    var msgType = parsedFix.GetField(35);
                    
                    if (msgType == "8") // Execution Report
                    {
                        ProcessExecutionReport(parsedFix);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing received message: {Message}", message);
            }
        }

        private void ProcessExecutionReport(FIXMessage executionReport)
        {
            var orderId = executionReport.GetField(11);
            var execType = executionReport.GetField(150);
            var ordStatus = executionReport.GetField(39);
            
            _logger.LogInformation($"Execution Report - OrderID: {orderId}, ExecType: {execType}, OrdStatus: {ordStatus}");
            
            // Process the execution report and update trade records
        }

        public async Task DisconnectAsync()
        {
            if (_hubConnection != null)
            {
                await _hubConnection.StopAsync();
                await _hubConnection.DisposeAsync();
            }

            // Logout
            if (_cookies != null)
            {
                using var client = new HttpClient(new HttpClientHandler { CookieContainer = _cookies });
                var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{_baseUrl}api/v1/Authentication/logout"));
                var response = await client.SendAsync(request);
                _logger.LogInformation($"Logout result: {response.StatusCode}");
            }
        }

        private string EncryptString(string publicKeyPath, string input)
        {
            string result;
            StreamReader fileReader = null;
            try
            {
                var plainTextBytes = Encoding.UTF8.GetBytes(input);
                fileReader = File.OpenText(publicKeyPath);
                var pr = new PemReader(fileReader);
                var keys = (RsaKeyParameters)pr.ReadObject();
                var eng = new OaepEncoding(new RsaEngine());

                eng.Init(true, keys);

                var length = plainTextBytes.Length;
                var blockSize = eng.GetInputBlockSize();
                var cipherTextBytes = new List<byte>();
                for (var chunkPosition = 0; chunkPosition < length; chunkPosition += blockSize)
                {
                    var chunkSize = Math.Min(blockSize, length - chunkPosition);
                    cipherTextBytes.AddRange(eng.ProcessBlock(plainTextBytes, chunkPosition, chunkSize));
                }

                result = Convert.ToBase64String(cipherTextBytes.ToArray());
            }
            finally
            {
                fileReader?.Dispose();
            }
            return result;
        }
    }
}