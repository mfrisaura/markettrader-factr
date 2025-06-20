using FIXLinkTradingServer.Models;
using System.Collections.Concurrent;

namespace FIXLinkTradingServer.Services
{
    public interface IOrderProcessingService
    {
        Task ProcessOrderFromPCSAsync(FIXMessage orderMessage);
        Task ProcessExecutionReportFromMarketTraderAsync(FIXMessage executionReport);
        Task ProcessOrderCancelFromPCSAsync(FIXMessage cancelMessage);
        Task<PendingOrder> GetPendingOrderAsync(string clientOrderId);
        Task<List<PendingOrder>> GetPendingOrdersForAccountAsync(string accountId);
        Task UpdateOrderStatusAsync(string clientOrderId, OrderStatus status);
    }

    public class OrderProcessingService : IOrderProcessingService
    {
        private readonly ILogger<OrderProcessingService> _logger;
        private readonly ICashBalanceService _cashBalanceService;
        private readonly IFIXLinkService _fixLinkService;
        private readonly ITradingService _tradingService;
        private readonly ConcurrentDictionary<string, PendingOrder> _pendingOrders = new();

        public OrderProcessingService(
            ILogger<OrderProcessingService> logger,
            ICashBalanceService cashBalanceService,
            IFIXLinkService fixLinkService,
            ITradingService tradingService)
        {
            _logger = logger;
            _cashBalanceService = cashBalanceService;
            _fixLinkService = fixLinkService;
            _tradingService = tradingService;
        }

        public async Task ProcessOrderFromPCSAsync(FIXMessage orderMessage)
        {
            var clientOrderId = orderMessage.ClOrdID;
            var symbol = orderMessage.Symbol;
            var side = orderMessage.Side;
            
            _logger.LogInformation($"Processing FIX order from PCS: ClOrdID={clientOrderId}, Symbol={symbol}, Side={side}");

            try
            {
                // Convert FIX message to internal trade request
                var tradeRequest = ConvertFIXToTradeRequest(orderMessage);
                
                // Get account and symbol balance
                var account = await _tradingService.GetAccountAsync(tradeRequest.AccountId);
                if (account == null)
                {
                    // Send rejection back to PCS
                    await SendRejectionToPCSAsync(clientOrderId, "Account not found", orderMessage);
                    return;
                }

                var symbolBalance = account.GetSymbolBalance(tradeRequest.Symbol);

                // Create pending order
                var pendingOrder = new PendingOrder
                {
                    ClientOrderId = clientOrderId,
                    AccountId = tradeRequest.AccountId,
                    Symbol = tradeRequest.Symbol,
                    Type = tradeRequest.Type,
                    RequestedQuantity = tradeRequest.Quantity ?? 0,
                    RequestedValue = tradeRequest.DollarAmount ?? (tradeRequest.Quantity ?? 0) * await _cashBalanceService.GetEODPriceAsync(tradeRequest.Symbol),
                    Status = OrderStatus.PendingNew
                };

                // Process through cash balance logic
                var processResult = await _cashBalanceService.ProcessOrderAsync(symbolBalance, pendingOrder);
                
                if (processResult.Success)
                {
                    // Store pending order
                    _pendingOrders.TryAdd(pendingOrder.ClientOrderId, pendingOrder);

                    // Send confirmation back to PCS
                    var confirmation = new PCSTradeResponse
                    {
                        ClientOrderId = clientOrderId,
                        OrderId = pendingOrder.OrderId,
                        Success = true,
                        Status = "Accepted",
                        Message = processResult.Message,
                        CashCovered = processResult.CashCovered,
                        NewCashBalance = symbolBalance.TradeCashBalance
                    };

                    await _fixLinkService.SendConfirmationToPCSAsync(confirmation, clientOrderId);

                    // Send to MarketTrader if required
                    if (processResult.ShouldSendToMarket)
                    {
                        _logger.LogInformation($"Sending order {clientOrderId} to MarketTrader");
                        var marketResult = await _fixLinkService.SendOrderToMarketTraderAsync(pendingOrder);
                        
                        if (marketResult.Success)
                        {
                            pendingOrder.MarketOrderId = marketResult.OrderId;
                            pendingOrder.SentToMarket = true;
                            pendingOrder.SentToMarketTime = DateTime.Now;
                            pendingOrder.Status = OrderStatus.New;
                        }
                        else
                        {
                            pendingOrder.Status = OrderStatus.Rejected;
                            pendingOrder.Notes = $"MarketTrader rejected: {marketResult.Message}";
                            
                            // Send rejection to PCS
                            await SendRejectionToPCSAsync(clientOrderId, pendingOrder.Notes, orderMessage);
                        }
                    }
                    else
                    {
                        // Order was handled internally (covered by cash or too small)
                        pendingOrder.Status = OrderStatus.Filled;
                        pendingOrder.ExecutedQuantity = pendingOrder.RequestedQuantity;
                        pendingOrder.ExecutedValue = pendingOrder.RequestedValue;
                        
                        // Create synthetic execution for internal handling
                        var syntheticExecution = new Execution
                        {
                            ExecutionId = Guid.NewGuid().ToString(),
                            OrderId = pendingOrder.OrderId,
                            Quantity = pendingOrder.RequestedQuantity,
                            Price = pendingOrder.RequestedValue / Math.Max(pendingOrder.RequestedQuantity, 1),
                            ExecutionTime = DateTime.Now,
                            Side = side
                        };

                        pendingOrder.Executions.Add(syntheticExecution);
                        
                        // Send execution report to PCS
                        await _fixLinkService.SendExecutionReportToPCSAsync(pendingOrder, syntheticExecution);
                        
                        // Archive completed order
                        _pendingOrders.TryRemove(clientOrderId, out _);
                        await _tradingService.ArchiveCompletedOrderAsync(pendingOrder);
                    }
                }
                else
                {
                    // Cash balance logic rejected the order
                    await SendRejectionToPCSAsync(clientOrderId, processResult.Message, orderMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error processing order from PCS: {clientOrderId}");
                await SendRejectionToPCSAsync(clientOrderId, "Internal processing error", orderMessage);
            }
        }

        public async Task ProcessExecutionReportFromMarketTraderAsync(FIXMessage executionReport)
        {
            var clientOrderId = executionReport.ClOrdID;
            var orderStatus = executionReport.OrdStatus;
            var execType = executionReport.GetField(150); // ExecType

            _logger.LogInformation($"Processing execution report from MarketTrader: ClOrdID={clientOrderId}, OrdStatus={orderStatus}, ExecType={execType}");

            if (_pendingOrders.TryGetValue(clientOrderId, out var pendingOrder))
            {
                // Parse execution details
                var execQty = decimal.TryParse(executionReport.GetField(32), out var qty) ? qty : 0; // LastQty
                var execPrice = decimal.TryParse(executionReport.GetField(31), out var price) ? price : 0; // LastPx
                var cumQty = decimal.TryParse(executionReport.GetField(14), out var cumulative) ? cumulative : 0; // CumQty
                var avgPx = decimal.TryParse(executionReport.GetField(6), out var avgPrice) ? avgPrice : 0; // AvgPx

                if (execQty > 0 && execPrice > 0)
                {
                    // Add execution
                    var execution = new Execution
                    {
                        ExecutionId = executionReport.GetField(17), // ExecID
                        OrderId = pendingOrder.OrderId,
                        Quantity = execQty,
                        Price = execPrice,
                        ExecutionTime = DateTime.Now,
                        Side = executionReport.Side
                    };
                    
                    pendingOrder.Executions.Add(execution);
                    pendingOrder.ExecutedQuantity = cumQty;
                    pendingOrder.ExecutedValue = pendingOrder.Executions.Sum(e => e.Value);
                    pendingOrder.LastExecutionTime = DateTime.Now;

                    // Forward execution report to PCS
                    await _fixLinkService.SendExecutionReportToPCSAsync(pendingOrder, execution);
                }

                // Update order status
                pendingOrder.Status = ParseOrderStatus(orderStatus);

                // Process cash adjustments based on execution
                if (pendingOrder.Status == OrderStatus.Filled || pendingOrder.Status == OrderStatus.PartiallyFilled)
                {
                    await _cashBalanceService.ProcessExecutionAsync(pendingOrder);
                }

                // Remove from pending if completely filled or rejected
                if (pendingOrder.Status == OrderStatus.Filled || pendingOrder.Status == OrderStatus.Rejected)
                {
                    _pendingOrders.TryRemove(clientOrderId, out _);
                    
                    // Archive to trade history
                    await _tradingService.ArchiveCompletedOrderAsync(pendingOrder);
                }
            }
            else
            {
                _logger.LogWarning($"Received execution report for unknown order: {clientOrderId}");
            }
        }

        public async Task ProcessOrderCancelFromPCSAsync(FIXMessage cancelMessage)
        {
            var origClientOrderId = cancelMessage.GetField(41); // OrigClOrdID
            var clientOrderId = cancelMessage.ClOrdID;

            _logger.LogInformation($"Processing cancel request from PCS: OrigClOrdID={origClientOrderId}, ClOrdID={clientOrderId}");

            if (_pendingOrders.TryGetValue(origClientOrderId, out var pendingOrder))
            {
                if (pendingOrder.SentToMarket)
                {
                    // Forward cancel request to MarketTrader
                    // This would involve creating a cancel message and sending it via FIXLink
                    _logger.LogInformation($"Forwarding cancel request to MarketTrader for order {origClientOrderId}");
                    // TODO: Implement cancel forwarding to MarketTrader
                }
                else
                {
                    // Order hasn't been sent to market yet, we can cancel it directly
                    pendingOrder.Status = OrderStatus.Canceled;
                    
                    // Release any allocated cash
                    var account = await _tradingService.GetAccountAsync(pendingOrder.AccountId);
                    if (account != null)
                    {
                        var symbolBalance = account.GetSymbolBalance(pendingOrder.Symbol);
                        symbolBalance.PendingCashReduction -= pendingOrder.CashAllocated;
                    }

                    // Send cancel confirmation to PCS
                    await SendCancelConfirmationToPCSAsync(pendingOrder, clientOrderId);

                    // Remove from pending orders
                    _pendingOrders.TryRemove(origClientOrderId, out _);
                }
            }
            else
            {
                // Send cancel reject to PCS
                await SendCancelRejectToPCSAsync(origClientOrderId, clientOrderId, "Order not found");
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

            // Extract account from FIX message
            request.AccountId = fixMessage.GetField(1) ?? // Account field
                               ExtractAccountFromClOrdID(fixMessage.ClOrdID) ?? 
                               "ACCT001"; // Default account

            // Determine trade type based on Side and other fields
            var side = fixMessage.Side;
            var orderQty = decimal.TryParse(fixMessage.GetField(38), out var qty) ? qty : 0; // OrderQty
            var dollarAmount = decimal.TryParse(fixMessage.GetField(110), out var dollars) ? dollars : 0; // Custom dollar field

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

        private string ExtractAccountFromClOrdID(string clOrdID)
        {
            // Extract account ID from client order ID if it follows a pattern
            // Example: "ACCT001_20231201_001" -> "ACCT001"
            if (!string.IsNullOrEmpty(clOrdID) && clOrdID.Contains('_'))
            {
                return clOrdID.Split('_')[0];
            }
            return null;
        }

        private async Task SendRejectionToPCSAsync(string clientOrderId, string reason, FIXMessage originalOrder)
        {
            var rejection = new PCSTradeResponse
            {
                ClientOrderId = clientOrderId,
                Success = false,
                Status = "Rejected",
                Message = reason
            };

            await _fixLinkService.SendConfirmationToPCSAsync(rejection, clientOrderId);
            _logger.LogInformation($"Sent rejection to PCS for order {clientOrderId}: {reason}");
        }

        private async Task SendCancelConfirmationToPCSAsync(PendingOrder order, string cancelClOrdID)
        {
            // Create cancel confirmation (would be a specific FIX message type)
            _logger.LogInformation($"Sending cancel confirmation to PCS for order {order.ClientOrderId}");
            // TODO: Implement cancel confirmation FIX message
        }

        private async Task SendCancelRejectToPCSAsync(string origClOrdID, string clOrdID, string reason)
        {
            // Create cancel reject (would be a specific FIX message type)
            _logger.LogWarning($"Sending cancel reject to PCS: OrigClOrdID={origClOrdID}, Reason={reason}");
            // TODO: Implement cancel reject FIX message
        }

        private OrderStatus ParseOrderStatus(string ordStatus)
        {
            return ordStatus switch
            {
                "0" => OrderStatus.New,
                "1" => OrderStatus.PartiallyFilled,
                "2" => OrderStatus.Filled,
                "4" => OrderStatus.Canceled,
                "8" => OrderStatus.Rejected,
                _ => OrderStatus.New
            };
        }

        public async Task<PendingOrder> GetPendingOrderAsync(string clientOrderId)
        {
            await Task.CompletedTask;
            return _pendingOrders.TryGetValue(clientOrderId, out var order) ? order : null;
        }

        public async Task<List<PendingOrder>> GetPendingOrdersForAccountAsync(string accountId)
        {
            await Task.CompletedTask;
            return _pendingOrders.Values.Where(o => o.AccountId == accountId).ToList();
        }

        public async Task UpdateOrderStatusAsync(string clientOrderId, OrderStatus status)
        {
            await Task.CompletedTask;
            if (_pendingOrders.TryGetValue(clientOrderId, out var order))
            {
                order.Status = status;
                _logger.LogInformation($"Updated order {clientOrderId} status to {status}");
            }
        }
    }
}