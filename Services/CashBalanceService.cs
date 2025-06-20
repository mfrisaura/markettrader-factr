using FIXLinkTradingServer.Models;

namespace FIXLinkTradingServer.Services
{
    public interface ICashBalanceService
    {
        Task<TradeResponse> ProcessShareSellAsync(Account account, Trade trade);
        Task<TradeResponse> ProcessDollarSellAsync(Account account, Trade trade);
        Task<TradeResponse> ProcessSharePurchaseAsync(Account account, Trade trade);
        Task<TradeResponse> ProcessDollarPurchaseAsync(Account account, Trade trade);
        Task UpdateTradeCashBalanceAsync(string accountId, decimal adjustment, string reason);
        Task<bool> CheckCashThresholdAsync(Account account);
    }

    public class CashBalanceService : ICashBalanceService
    {
        private readonly ILogger<CashBalanceService> _logger;
        private readonly IFIXLinkService _fixLinkService;

        public CashBalanceService(ILogger<CashBalanceService> logger, IFIXLinkService fixLinkService)
        {
            _logger = logger;
            _fixLinkService = fixLinkService;
        }

        public async Task<TradeResponse> ProcessShareSellAsync(Account account, Trade trade)
        {
            // Implementation of Share Sell logic from your workflow
            _logger.LogInformation($"Processing Share Sell for {trade.RequestedQuantity} shares of {trade.Symbol}");
            
            // Calculate fractional shares value using EOD price
            var fractionalShares = trade.RequestedQuantity % 1;
            var wholeShares = Math.Floor(trade.RequestedQuantity);
            var fractionalValue = fractionalShares * trade.EODPrice;

            var response = new TradeResponse
            {
                TradeId = trade.TradeId,
                Success = true
            };

            // Check if we have enough trade cash for fractional shares
            if (fractionalShares > 0 && account.TradeCashBalance >= fractionalValue)
            {
                // Cover fractional shares with trade cash
                trade.CashCovered = fractionalValue;
                trade.ExecutedQuantity = wholeShares;
                trade.Notes = $"Fractional shares ({fractionalShares}) covered by trade cash (${fractionalValue:F2})";
                
                // Send whole shares to BNY
                if (wholeShares > 0)
                {
                    var bnyResult = await _fixLinkService.SendTradeToMarketAsync(trade.Symbol, wholeShares, "2"); // Sell
                    trade.ExecutedValue = bnyResult.ExecutedValue;
                    trade.ExecutedPrice = bnyResult.ExecutedPrice;
                    trade.SentToBNY = true;
                    trade.Status = TradeStatus.ExecutedAtMarket;
                }
                else
                {
                    trade.Status = TradeStatus.CoveredByCash;
                }

                // Reduce trade cash
                account.TradeCashBalance -= fractionalValue;
                
                response.CashCovered = fractionalValue;
                response.ExecutedQuantity = trade.ExecutedQuantity;
                response.ExecutedValue = trade.ExecutedValue;
                response.Status = trade.Status;
                response.NewCashBalance = account.TradeCashBalance;
            }
            else if (fractionalShares > 0)
            {
                // Insufficient trade cash
                trade.Status = TradeStatus.Rejected;
                trade.Notes = $"Insufficient trade cash (${account.TradeCashBalance:F2}) for fractional shares requiring ${fractionalValue:F2}";
                response.Success = false;
                response.Message = trade.Notes;
                response.Status = TradeStatus.Rejected;
            }
            else
            {
                // No fractional shares, send all to BNY
                var bnyResult = await _fixLinkService.SendTradeToMarketAsync(trade.Symbol, trade.RequestedQuantity, "2");
                trade.ExecutedValue = bnyResult.ExecutedValue;
                trade.ExecutedPrice = bnyResult.ExecutedPrice;
                trade.ExecutedQuantity = bnyResult.ExecutedQuantity;
                trade.SentToBNY = true;
                trade.Status = TradeStatus.ExecutedAtMarket;
                
                response.ExecutedQuantity = trade.ExecutedQuantity;
                response.ExecutedValue = trade.ExecutedValue;
                response.Status = trade.Status;
            }

            await CheckCashThresholdAsync(account);
            return response;
        }

        public async Task<TradeResponse> ProcessDollarSellAsync(Account account, Trade trade)
        {
            _logger.LogInformation($"Processing Dollar Sell for ${trade.RequestedValue}");
            
            // Calculate amount to send to BNY (requested amount minus available trade cash up to limit)
            var tradeCashToUse = Math.Min(account.TradeCashBalance, Math.Min(account.MaxTradeCashUsage, trade.RequestedValue));
            var amountToBNY = trade.RequestedValue - tradeCashToUse;

            var response = new TradeResponse
            {
                TradeId = trade.TradeId,
                Success = true
            };

            if (amountToBNY > 0)
            {
                // Send reduced amount to BNY
                var bnyResult = await _fixLinkService.SendDollarTradeToMarketAsync(trade.Symbol, amountToBNY, "2"); // Sell
                trade.ExecutedValue = bnyResult.ExecutedValue;
                trade.ExecutedQuantity = bnyResult.ExecutedQuantity;
                trade.ExecutedPrice = bnyResult.ExecutedPrice;
                trade.SentToBNY = true;
                
                // Calculate cash adjustment (difference between what we sent and what was executed)
                var cashAdjustment = amountToBNY - trade.ExecutedValue;
                trade.CashAdjustment = cashAdjustment;
                
                // Update trade cash balance
                account.TradeCashBalance -= tradeCashToUse; // Remove what we used
                account.TradeCashBalance += cashAdjustment; // Add back the difference
                
                trade.Status = TradeStatus.ExecutedAtMarket;
                trade.Notes = $"Used ${tradeCashToUse:F2} trade cash, sent ${amountToBNY:F2} to BNY, cash adjustment: ${cashAdjustment:F2}";
            }
            else
            {
                // Entire amount covered by trade cash
                trade.CashCovered = trade.RequestedValue;
                trade.ExecutedValue = trade.RequestedValue;
                trade.Status = TradeStatus.CoveredByCash;
                account.TradeCashBalance -= trade.RequestedValue;
                trade.Notes = $"Entire amount (${trade.RequestedValue:F2}) covered by trade cash";
            }

            response.ExecutedValue = trade.ExecutedValue;
            response.ExecutedQuantity = trade.ExecutedQuantity;
            response.CashCovered = tradeCashToUse;
            response.CashAdjustment = trade.CashAdjustment;
            response.Status = trade.Status;
            response.NewCashBalance = account.TradeCashBalance;

            await CheckCashThresholdAsync(account);
            return response;
        }

        public async Task<TradeResponse> ProcessSharePurchaseAsync(Account account, Trade trade)
        {
            _logger.LogInformation($"Processing Share Purchase for {trade.RequestedQuantity} shares of {trade.Symbol}");
            
            // Calculate value using EOD price
            var shareValue = trade.RequestedQuantity * trade.EODPrice;
            
            var response = new TradeResponse
            {
                TradeId = trade.TradeId,
                Success = true
            };

            // Check if trade should be sent to BNY based on size and rules
            if (ShouldSendToBNY(trade.RequestedQuantity, shareValue))
            {
                var bnyResult = await _fixLinkService.SendTradeToMarketAsync(trade.Symbol, trade.RequestedQuantity, "1"); // Buy
                trade.ExecutedValue = bnyResult.ExecutedValue;
                trade.ExecutedQuantity = bnyResult.ExecutedQuantity;
                trade.ExecutedPrice = bnyResult.ExecutedPrice;
                trade.SentToBNY = true;
                trade.Status = TradeStatus.ExecutedAtMarket;
            }
            else
            {
                // Don't send to BNY, calculate value and add to trade cash
                trade.ExecutedValue = shareValue;
                trade.ExecutedQuantity = trade.RequestedQuantity;
                trade.ExecutedPrice = trade.EODPrice;
                trade.SentToBNY = false;
                trade.Status = TradeStatus.CoveredByCash;
                
                // Add value to available trade cash
                account.TradeCashBalance += shareValue;
                trade.Notes = $"Small trade not sent to BNY. Value ${shareValue:F2} added to trade cash";
            }

            response.ExecutedQuantity = trade.ExecutedQuantity;
            response.ExecutedValue = trade.ExecutedValue;
            response.Status = trade.Status;
            response.NewCashBalance = account.TradeCashBalance;

            await CheckCashThresholdAsync(account);
            return response;
        }

        public async Task<TradeResponse> ProcessDollarPurchaseAsync(Account account, Trade trade)
        {
            _logger.LogInformation($"Processing Dollar Purchase for ${trade.RequestedValue}");
            
            // Add available trade cash to purchase amount
            var totalAmountToBNY = trade.RequestedValue + account.TradeCashBalance;
            
            var response = new TradeResponse
            {
                TradeId = trade.TradeId,
                Success = true
            };

            // Send enhanced amount to BNY
            var bnyResult = await _fixLinkService.SendDollarTradeToMarketAsync(trade.Symbol, totalAmountToBNY, "1"); // Buy
            trade.ExecutedValue = bnyResult.ExecutedValue;
            trade.ExecutedQuantity = bnyResult.ExecutedQuantity;
            trade.ExecutedPrice = bnyResult.ExecutedPrice;
            trade.SentToBNY = true;
            
            // Calculate cash adjustment (difference between what we sent and what was executed)
            var cashAdjustment = totalAmountToBNY - trade.ExecutedValue;
            trade.CashAdjustment = cashAdjustment;
            
            // Update trade cash balance (use all existing cash + add back the difference)
            account.TradeCashBalance = cashAdjustment;
            
            trade.Status = TradeStatus.ExecutedAtMarket;
            trade.Notes = $"Sent ${totalAmountToBNY:F2} (${trade.RequestedValue:F2} + ${account.TradeCashBalance:F2} trade cash) to BNY, " +
                         $"executed ${trade.ExecutedValue:F2}, cash adjustment: ${cashAdjustment:F2}";

            response.ExecutedValue = trade.ExecutedValue;
            response.ExecutedQuantity = trade.ExecutedQuantity;
            response.CashAdjustment = trade.CashAdjustment;
            response.Status = trade.Status;
            response.NewCashBalance = account.TradeCashBalance;

            await CheckCashThresholdAsync(account);
            return response;
        }

        public async Task UpdateTradeCashBalanceAsync(string accountId, decimal adjustment, string reason)
        {
            _logger.LogInformation($"Updating trade cash balance for account {accountId}: {adjustment:F2} - {reason}");
            // Implementation would update the account balance
            await Task.CompletedTask;
        }

        public async Task<bool> CheckCashThresholdAsync(Account account)
        {
            if (account.TradeCashBalance <= account.CashThreshold && !account.ThresholdAlertSent)
            {
                _logger.LogWarning($"Trade cash balance for account {account.AccountId} is below threshold: ${account.TradeCashBalance:F2}");
                account.ThresholdAlertSent = true;
                // Send alert to PCS or admin
                return true;
            }
            else if (account.TradeCashBalance > account.CashThreshold)
            {
                account.ThresholdAlertSent = false;
            }
            return false;
        }

        private bool ShouldSendToBNY(decimal quantity, decimal value)
        {
            // Business rules to determine if trade should go to BNY
            // Example: Don't send very small trades
            return value > 50.00m && quantity > 0.5m;
        }
    }
}