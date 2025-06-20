using FIXLinkTradingServer.Models;

namespace FIXLinkTradingServer.Services
{
    public interface ICashBalanceService
    {
        Task<CashProcessingResult> ProcessOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order);
        Task ProcessExecutionAsync(PendingOrder order);
        Task<bool> CheckCashThresholdAsync(AccountSymbolBalance symbolBalance);
        Task UpdateTradeCashBalanceAsync(string accountId, string symbol, decimal newBalance);
        Task<decimal> GetEODPriceAsync(string symbol);
    }

    public class CashBalanceService : ICashBalanceService
    {
        private readonly ILogger<CashBalanceService> _logger;
        private readonly Dictionary<string, decimal> _eodPrices = new();

        public CashBalanceService(ILogger<CashBalanceService> logger)
        {
            _logger = logger;
            InitializeEODPrices();
        }

        private void InitializeEODPrices()
        {
            // Initialize with sample EOD prices - in production, this would come from market data feed
            _eodPrices["SSNC"] = 205.00m;
            _eodPrices["AAPL"] = 150.00m;
            _eodPrices["MSFT"] = 300.00m;
        }

        public async Task<CashProcessingResult> ProcessOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order)
        {
            _logger.LogInformation($"Processing {order.Type} order for {order.Symbol} in account {order.AccountId}");

            var eodPrice = await GetEODPriceAsync(order.Symbol);
            var result = new CashProcessingResult { Success = true };

            switch (order.Type)
            {
                case TradeType.ShareSell:
                    result = await ProcessShareSellOrderAsync(symbolBalance, order, eodPrice);
                    break;

                case TradeType.DollarSell:
                    result = await ProcessDollarSellOrderAsync(symbolBalance, order);
                    break;

                case TradeType.SharePurchase:
                    result = await ProcessSharePurchaseOrderAsync(symbolBalance, order, eodPrice);
                    break;

                case TradeType.DollarPurchase:
                    result = await ProcessDollarPurchaseOrderAsync(symbolBalance, order);
                    break;

                default:
                    result.Success = false;
                    result.Message = $"Unsupported trade type: {order.Type}";
                    break;
            }

            // Update last activity time
            symbolBalance.LastUpdated = DateTime.Now;

            // Check threshold after processing
            await CheckCashThresholdAsync(symbolBalance);

            return result;
        }

        private async Task<CashProcessingResult> ProcessShareSellOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order, decimal eodPrice)
        {
            // SS&C Share Sell Logic:
            // 1. Calculate fractional shares value using EOD price
            // 2. Check if trade cash can cover fractional shares
            // 3. Send whole shares to BNY, cover fractional with cash

            var fractionalShares = order.RequestedQuantity % 1;
            var wholeShares = Math.Floor(order.RequestedQuantity);
            var fractionalValue = fractionalShares * eodPrice;

            var result = new CashProcessingResult { Success = true };

            if (fractionalShares > 0)
            {
                // Check if we have enough available cash for fractional shares
                if (symbolBalance.AvailableCash >= fractionalValue)
                {
                    // Cover fractional shares with trade cash
                    order.CashAllocated = fractionalValue;
                    order.SentQuantity = wholeShares;
                    order.SentValue = wholeShares * eodPrice;
                    order.Notes = $"Fractional shares ({fractionalShares:F3}) covered by trade cash (${fractionalValue:F2})";

                    // Reserve the cash
                    symbolBalance.PendingCashReduction += fractionalValue;

                    // Send whole shares to market if any
                    result.ShouldSendToMarket = wholeShares > 0;
                    result.CashCovered = fractionalValue;
                    result.Message = order.Notes;

                    _logger.LogInformation($"Share Sell: Covering {fractionalShares:F3} fractional shares with ${fractionalValue:F2} trade cash");
                }
                else
                {
                    // Insufficient trade cash for fractional shares
                    result.Success = false;
                    result.Message = $"Insufficient trade cash (${symbolBalance.AvailableCash:F2}) for fractional shares requiring ${fractionalValue:F2}";
                    order.Notes = result.Message;
                }
            }
            else
            {
                // No fractional shares, send entire quantity to market
                order.SentQuantity = order.RequestedQuantity;
                order.SentValue = order.RequestedQuantity * eodPrice;
                order.CashAllocated = 0;
                result.ShouldSendToMarket = true;
                result.Message = "Sending entire quantity to market";
            }

            return result;
        }

        private async Task<CashProcessingResult> ProcessDollarSellOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order)
        {
            // SS&C Dollar Sell Logic:
            // 1. Take available trade cash and subtract from dollar amount before sending to BNY
            // 2. There is a limit in the amount of trade cash that can be used (MaxTradeCashUsage)

            var maxCashUsage = Math.Min(symbolBalance.AvailableCash, order.RequestedValue);
            var tradeCashToUse = Math.Min(maxCashUsage, symbolBalance.Account?.MaxTradeCashUsage ?? 1000m);
            var amountToMarket = order.RequestedValue - tradeCashToUse;

            var result = new CashProcessingResult { Success = true };

            if (amountToMarket > 0)
            {
                // Send reduced amount to market
                order.SentValue = amountToMarket;
                order.CashAllocated = tradeCashToUse;
                order.Notes = $"Using ${tradeCashToUse:F2} trade cash, sending ${amountToMarket:F2} to market";

                // Reserve the cash
                symbolBalance.PendingCashReduction += tradeCashToUse;

                result.ShouldSendToMarket = true;
                result.CashCovered = tradeCashToUse;
                result.Message = order.Notes;

                _logger.LogInformation($"Dollar Sell: Using ${tradeCashToUse:F2} trade cash, sending ${amountToMarket:F2} to market");
            }
            else
            {
                // Entire amount covered by trade cash
                order.SentValue = 0;
                order.CashAllocated = order.RequestedValue;
                order.Notes = $"Entire amount (${order.RequestedValue:F2}) covered by trade cash";

                // Reserve the cash
                symbolBalance.PendingCashReduction += order.RequestedValue;

                result.ShouldSendToMarket = false;
                result.CashCovered = order.RequestedValue;
                result.Message = order.Notes;

                _logger.LogInformation($"Dollar Sell: Entire amount ${order.RequestedValue:F2} covered by trade cash");
            }

            return result;
        }

        private async Task<CashProcessingResult> ProcessSharePurchaseOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order, decimal eodPrice)
        {
            // SS&C Share Purchase Logic:
            // 1. Due to the size of the trade and rules, decide whether to send to BNY
            // 2. If not sending to BNY, calculate value using EOD price and add to trade cash

            var shareValue = order.RequestedQuantity * eodPrice;
            var result = new CashProcessingResult { Success = true };

            // Business rule: Don't send very small trades to market
            var shouldSendToMarket = ShouldSendToBNY(order.RequestedQuantity, shareValue);

            if (shouldSendToMarket)
            {
                // Send to market
                order.SentQuantity = order.RequestedQuantity;
                order.SentValue = shareValue;
                order.CashAllocated = 0;
                order.Notes = $"Sending {order.RequestedQuantity:F3} shares to market";

                result.ShouldSendToMarket = true;
                result.Message = order.Notes;

                _logger.LogInformation($"Share Purchase: Sending {order.RequestedQuantity:F3} shares (${shareValue:F2}) to market");
            }
            else
            {
                // Don't send to market, add value to trade cash
                order.SentQuantity = 0;
                order.SentValue = 0;
                order.CashAllocated = 0;
                order.Notes = $"Small trade not sent to market. Value ${shareValue:F2} will be added to trade cash";

                // We'll add to cash balance when this "executes" (immediately)
                result.ShouldSendToMarket = false;
                result.CashAdjustment = shareValue;
                result.Message = order.Notes;

                _logger.LogInformation($"Share Purchase: Small trade {order.RequestedQuantity:F3} shares (${shareValue:F2}) not sent to market");
            }

            return result;
        }

        private async Task<CashProcessingResult> ProcessDollarPurchaseOrderAsync(AccountSymbolBalance symbolBalance, PendingOrder order)
        {
            // SS&C Dollar Purchase Logic:
            // 1. Add available trade cash to purchase amount and send to BNY

            var totalAmountToMarket = order.RequestedValue + symbolBalance.AvailableCash;
            
            var result = new CashProcessingResult { Success = true };

            order.SentValue = totalAmountToMarket;
            order.CashAllocated = symbolBalance.AvailableCash; // We're using all available cash
            order.Notes = $"Sending ${totalAmountToMarket:F2} (${order.RequestedValue:F2} + ${symbolBalance.AvailableCash:F2} trade cash) to market";

            // Reserve all available cash
            symbolBalance.PendingCashReduction += symbolBalance.AvailableCash;

            result.ShouldSendToMarket = true;
            result.CashCovered = symbolBalance.AvailableCash;
            result.Message = order.Notes;

            _logger.LogInformation($"Dollar Purchase: Sending ${totalAmountToMarket:F2} (includes ${symbolBalance.AvailableCash:F2} trade cash) to market");

            return result;
        }

        public async Task ProcessExecutionAsync(PendingOrder order)
        {
            _logger.LogInformation($"Processing execution for order {order.ClientOrderId}: {order.ExecutedQuantity} @ ${order.ExecutedValue:F2}");

            // Get the symbol balance to update cash
            // Note: We'll need a way to get the account/symbol balance here
            // For now, we'll log the cash adjustment that needs to be made

            var cashAdjustment = CalculateCashAdjustment(order);
            
            if (cashAdjustment != 0)
            {
                _logger.LogInformation($"Cash adjustment for order {order.ClientOrderId}: ${cashAdjustment:F2}");
                // TODO: Apply cash adjustment to the appropriate symbol balance
            }

            await Task.CompletedTask;
        }

        private decimal CalculateCashAdjustment(PendingOrder order)
        {
            decimal adjustment = 0;

            switch (order.Type)
            {
                case TradeType.ShareSell:
                    // Release allocated cash, no additional adjustment needed
                    adjustment = 0;
                    break;

                case TradeType.DollarSell:
                    // Add back the difference between what we sent and what was executed
                    adjustment = order.SentValue - order.ExecutedValue;
                    break;

                case TradeType.SharePurchase:
                    if (!order.SentToMarket)
                    {
                        // Add the calculated share value to cash balance
                        var eodPrice = GetEODPriceAsync(order.Symbol).Result;
                        adjustment = order.RequestedQuantity * eodPrice;
                    }
                    break;

                case TradeType.DollarPurchase:
                    // Add back the difference between what we sent and what was executed
                    adjustment = order.SentValue - order.ExecutedValue;
                    break;
            }

            return adjustment;
        }

        public async Task<bool> CheckCashThresholdAsync(AccountSymbolBalance symbolBalance)
        {
            if (symbolBalance.TradeCashBalance <= symbolBalance.CashThreshold)
            {
                _logger.LogWarning($"Trade cash balance for {symbolBalance.AccountId}:{symbolBalance.Symbol} is below threshold: ${symbolBalance.TradeCashBalance:F2}");
                // TODO: Send alert to appropriate parties
                return true;
            }
            return false;
        }

        public async Task UpdateTradeCashBalanceAsync(string accountId, string symbol, decimal newBalance)
        {
            _logger.LogInformation($"Updating trade cash balance for {accountId}:{symbol} to ${newBalance:F2}");
            // TODO: Update the actual balance in storage
            await Task.CompletedTask;
        }

        public async Task<decimal> GetEODPriceAsync(string symbol)
        {
            await Task.CompletedTask;
            return _eodPrices.TryGetValue(symbol, out var price) ? price : 100.00m;
        }

        private bool ShouldSendToBNY(decimal quantity, decimal value)
        {
            // Business rules for determining if trade should go to market
            // Example: Don't send very small trades
            return value > 50.00m && quantity > 0.5m;
        }
    }
}