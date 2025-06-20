using Microsoft.AspNetCore.Mvc;
using FIXLinkTradingServer.Models;
using FIXLinkTradingServer.Services;

namespace FIXLinkTradingServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PCSController : ControllerBase
    {
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly ITradingService _tradingService;
        private readonly ILogger<PCSController> _logger;

        public PCSController(
            IOrderProcessingService orderProcessingService, 
            ITradingService tradingService,
            ILogger<PCSController> logger)
        {
            _orderProcessingService = orderProcessingService;
            _tradingService = tradingService;
            _logger = logger;
        }

        /// <summary>
        /// Receive new order from PCS
        /// </summary>
        [HttpPost("order")]
        public async Task<ActionResult<PCSTradeResponse>> ReceiveOrder([FromBody] PCSTradeRequest request)
        {
            try
            {
                _logger.LogInformation($"Received order from PCS: {request.ClientOrderId} for account {request.AccountId}");

                // Validate request
                if (string.IsNullOrEmpty(request.AccountId) || string.IsNullOrEmpty(request.Symbol) || 
                    string.IsNullOrEmpty(request.ClientOrderId))
                {
                    return BadRequest(new PCSTradeResponse
                    {
                        ClientOrderId = request.ClientOrderId,
                        Success = false,
                        Message = "Missing required fields: AccountId, Symbol, or ClientOrderId"
                    });
                }

                // Process the order through cash balance logic
                var response = await _orderProcessingService.ProcessOrderFromPCSAsync(request);
                
                _logger.LogInformation($"Order {request.ClientOrderId} processed: Success={response.Success}, Status={response.Status}");
                
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing order from PCS: {request?.ClientOrderId}");
                return StatusCode(500, new PCSTradeResponse
                {
                    ClientOrderId = request?.ClientOrderId,
                    Success = false,
                    Message = "Internal server error"
                });
            }
        }

        /// <summary>
        /// Receive FIX message from PCS (alternative to REST)
        /// </summary>
        [HttpPost("fix")]
        public async Task<ActionResult> ReceiveFIXMessage([FromBody] string fixMessage)
        {
            try
            {
                _logger.LogInformation($"Received FIX message from PCS: {fixMessage}");

                var parsedFix = FIXMessage.ParseFIXString(fixMessage);
                
                if (parsedFix.IsNewOrder())
                {
                    // Convert FIX message to PCS trade request
                    var request = ConvertFIXToTradeRequest(parsedFix);
                    var response = await _orderProcessingService.ProcessOrderFromPCSAsync(request);
                    
                    // Send FIX response back to PCS
                    var fixResponse = ConvertResponseToFIX(response);
                    return Ok(new { fixMessage = fixResponse.ToFIXString() });
                }
                else
                {
                    return BadRequest("Unsupported FIX message type");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing FIX message from PCS");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get account balance for PCS
        /// </summary>
        [HttpGet("account/{accountId}/balance")]
        public async Task<ActionResult> GetAccountBalance(string accountId, [FromQuery] string symbol = null)
        {
            try
            {
                var account = await _tradingService.GetAccountAsync(accountId);
                if (account == null)
                {
                    return NotFound($"Account {accountId} not found");
                }

                if (!string.IsNullOrEmpty(symbol))
                {
                    // Return balance for specific symbol
                    var symbolBalance = account.GetSymbolBalance(symbol);
                    return Ok(new
                    {
                        accountId,
                        symbol,
                        tradeCashBalance = symbolBalance.TradeCashBalance,
                        availableCash = symbolBalance.AvailableCash,
                        pendingCashReduction = symbolBalance.PendingCashReduction,
                        pendingOrdersCount = symbolBalance.PendingOrders.Count,
                        lastUpdated = symbolBalance.LastUpdated
                    });
                }
                else
                {
                    // Return all symbol balances
                    var balances = account.SymbolBalances.Select(sb => new
                    {
                        symbol = sb.Key,
                        tradeCashBalance = sb.Value.TradeCashBalance,
                        availableCash = sb.Value.AvailableCash,
                        pendingCashReduction = sb.Value.PendingCashReduction,
                        pendingOrdersCount = sb.Value.PendingOrders.Count
                    });

                    return Ok(new
                    {
                        accountId,
                        symbolBalances = balances,
                        lastUpdated = account.LastUpdated
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting account balance for {accountId}");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get pending orders for PCS
        /// </summary>
        [HttpGet("account/{accountId}/pending-orders")]
        public async Task<ActionResult> GetPendingOrders(string accountId)
        {
            try
            {
                var pendingOrders = await _orderProcessingService.GetPendingOrdersForAccountAsync(accountId);
                
                var orderSummaries = pendingOrders.Select(o => new
                {
                    clientOrderId = o.ClientOrderId,
                    marketOrderId = o.MarketOrderId,
                    symbol = o.Symbol,
                    type = o.Type.ToString(),
                    requestedQuantity = o.RequestedQuantity,
                    requestedValue = o.RequestedValue,
                    sentQuantity = o.SentQuantity,
                    sentValue = o.SentValue,
                    executedQuantity = o.ExecutedQuantity,
                    executedValue = o.ExecutedValue,
                    cashAllocated = o.CashAllocated,
                    status = o.Status.ToString(),
                    createdTime = o.CreatedTime,
                    sentToMarketTime = o.SentToMarketTime,
                    lastExecutionTime = o.LastExecutionTime,
                    notes = o.Notes
                });

                return Ok(new
                {
                    accountId,
                    pendingOrdersCount = pendingOrders.Count,
                    orders = orderSummaries
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting pending orders for account {accountId}");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Manually update cash balance (admin function)
        /// </summary>
        [HttpPost("account/{accountId}/symbol/{symbol}/cash-balance")]
        public async Task<ActionResult> UpdateCashBalance(string accountId, string symbol, [FromBody] decimal newBalance)
        {
            try
            {
                _logger.LogInformation($"Manual cash balance update: {accountId}:{symbol} to ${newBalance:F2}");
                
                var account = await _tradingService.GetAccountAsync(accountId);
                if (account == null)
                {
                    return NotFound($"Account {accountId} not found");
                }

                var symbolBalance = account.GetSymbolBalance(symbol);
                var oldBalance = symbolBalance.TradeCashBalance;
                
                symbolBalance.TradeCashBalance = newBalance;
                symbolBalance.LastUpdated = DateTime.Now;

                _logger.LogInformation($"Updated cash balance for {accountId}:{symbol} from ${oldBalance:F2} to ${newBalance:F2}");

                return Ok(new
                {
                    accountId,
                    symbol,
                    oldBalance,
                    newBalance,
                    availableCash = symbolBalance.AvailableCash,
                    updatedTime = symbolBalance.LastUpdated
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error updating cash balance for {accountId}:{symbol}");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Get end-of-day cash balance summary for PCS
        /// </summary>
        [HttpGet("eod-summary")]
        public async Task<ActionResult> GetEODSummary([FromQuery] string accountId = null)
        {
            try
            {
                var accounts = string.IsNullOrEmpty(accountId) 
                    ? await _tradingService.GetAllAccountsAsync()
                    : new List<Account> { await _tradingService.GetAccountAsync(accountId) }.Where(a => a != null).ToList();

                var summary = accounts.Select(account => new
                {
                    accountId = account.AccountId,
                    symbols = account.SymbolBalances.Select(sb => new
                    {
                        symbol = sb.Key,
                        startingBalance = sb.Value.StartingTradeCashBalance,
                        currentBalance = sb.Value.TradeCashBalance,
                        netChange = sb.Value.TradeCashBalance - sb.Value.StartingTradeCashBalance,
                        availableCash = sb.Value.AvailableCash,
                        pendingCash = sb.Value.PendingCashReduction,
                        tradesCount = account.AllTrades.Count(t => t.Symbol == sb.Key),
                        belowThreshold = sb.Value.TradeCashBalance <= sb.Value.CashThreshold
                    }),
                    totalTrades = account.AllTrades.Count,
                    lastActivity = account.LastUpdated
                });

                return Ok(new
                {
                    generatedTime = DateTime.Now,
                    accountSummaries = summary
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating EOD summary");
                return StatusCode(500, "Internal server error");
            }
        }

        private PCSTradeRequest ConvertFIXToTradeRequest(FIXMessage fixMessage)
        {
            var request = new PCSTradeRequest
            {
                ClientOrderId = fixMessage.ClOrdID,
                Symbol = fixMessage.Symbol,
                OriginalFIXMessage = fixMessage.ToFIXString()
            };

            // Determine trade type based on Side and other fields
            var side = fixMessage.Side;
            var orderQty = decimal.TryParse(fixMessage.GetField(38), out var qty) ? qty : 0; // OrderQty
            var dollarAmount = decimal.TryParse(fixMessage.GetField(110), out var dollars) ? dollars : 0; // Custom dollar field

            // Extract account from custom field or parse from ClOrdID
            request.AccountId = fixMessage.GetField(1) ?? "ACCT001"; // Account field or default

            if (dollarAmount > 0)
            {
                // Dollar-based order
                request.DollarAmount = dollarAmount;
                request.Type = side == "1" ? TradeType.DollarPurchase : TradeType.DollarSell;
            }
            else
            {
                // Share-based order
                request.Quantity = orderQty;
                request.Type = side == "1" ? TradeType.SharePurchase : TradeType.ShareSell;
            }

            return request;
        }

        private FIXMessage ConvertResponseToFIX(PCSTradeResponse response)
        {
            var fixResponse = new FIXMessage();
            
            // Execution Report message
            fixResponse.SetField(8, "FIX.4.2"); // BeginString
            fixResponse.SetField(35, "8"); // MsgType - Execution Report
            fixResponse.SetField(11, response.ClientOrderId); // ClOrdID
            fixResponse.SetField(37, response.OrderId); // OrderID
            fixResponse.SetField(39, response.Success ? "0" : "8"); // OrdStatus (New or Rejected)
            fixResponse.SetField(150, response.Success ? "0" : "8"); // ExecType (New or Rejected)
            fixResponse.SetField(60, DateTime.Now.ToString("yyyyMMdd-HH:mm:ss")); // TransactTime
            
            if (!response.Success)
            {
                fixResponse.SetField(58, response.Message); // Text - rejection reason
            }

            return fixResponse;
        }
    }
}