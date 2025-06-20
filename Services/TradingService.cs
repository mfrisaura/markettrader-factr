using FIXLinkTradingServer.Models;
using System.Collections.Concurrent;

namespace FIXLinkTradingServer.Services
{
    public interface ITradingService
    {
        Task<TradeResponse> ProcessTradeAsync(TradeRequest request);
        Task<Account> GetAccountAsync(string accountId);
        Task<List<Trade>> GetTradeHistoryAsync(string accountId);
        Task<decimal> GetEODPriceAsync(string symbol);
        Task UpdateTradeCashBalanceAsync(string accountId, decimal newBalance);
        Task<List<Account>> GetAllAccountsAsync();
    }

    public class TradingService : ITradingService
    {
        private readonly ILogger<TradingService> _logger;
        private readonly ICashBalanceService _cashBalanceService;
        private readonly IFIXLinkService _fixLinkService;
        private readonly ConcurrentDictionary<string, Account> _accounts = new();
        private readonly ConcurrentDictionary<string, decimal> _eodPrices = new();

        public TradingService(ILogger<TradingService> logger,
                             ICashBalanceService cashBalanceService,
                             IFIXLinkService fixLinkService)
        {
            _logger = logger;
            _cashBalanceService = cashBalanceService;
            _fixLinkService = fixLinkService;
            
            // Initialize with sample data
            InitializeSampleData();
        }

        private void InitializeSampleData()
        {
            // Create sample account with starting balance of $333.01 as per your example
            var sampleAccount = new Account
            {
                AccountId = "ACCT001",
                TradeCashBalance = 333.01m,
                StartingTradeCashBalance = 333.01m,
                CashThreshold = 50.00m,
                MaxTradeCashUsage = 1000.00m,
                LastUpdated = DateTime.Now
            };
            _accounts.TryAdd(sampleAccount.AccountId, sampleAccount);

            // Initialize sample EOD prices
            _eodPrices.TryAdd("SSNC", 205.00m);
            _eodPrices.TryAdd("AAPL", 150.00m);
            _eodPrices.TryAdd("MSFT", 300.00m);
        }

        public async Task<TradeResponse> ProcessTradeAsync(TradeRequest request)
        {
            _logger.LogInformation($"Processing trade request: {request.Type} for account {request.AccountId}");

            var account = await GetAccountAsync(request.AccountId);
            if (account == null)
            {
                return new TradeResponse
                {
                    Success = false,
                    Message = $"Account {request.AccountId} not found"
                };
            }

            // Create trade object
            var trade = new Trade
            {
                TradeId = Guid.NewGuid().ToString(),
                AccountId = request.AccountId,
                Symbol = request.Symbol,
                Type = request.Type,
                TradeTime = request.RequestTime,
                Status = TradeStatus.Pending
            };

            // Get EOD price for the symbol
            trade.EODPrice = await GetEODPriceAsync(request.Symbol);

            TradeResponse response;

            switch (request.Type)
            {
                case TradeType.ShareSell:
                    trade.RequestedQuantity = request.Quantity ?? 0;
                    trade.RequestedValue = trade.RequestedQuantity * trade.EODPrice;
                    response = await _cashBalanceService.ProcessShareSellAsync(account, trade);
                    break;

                case TradeType.DollarSell:
                    trade.RequestedValue = request.DollarAmount ?? 0;
                    response = await _cashBalanceService.ProcessDollarSellAsync(account, trade);
                    break;

                case TradeType.SharePurchase:
                    trade.RequestedQuantity = request.Quantity ?? 0;
                    trade.RequestedValue = trade.RequestedQuantity * trade.EODPrice;
                    response = await _cashBalanceService.ProcessSharePurchaseAsync(account, trade);
                    break;

                case TradeType.DollarPurchase:
                    trade.RequestedValue = request.DollarAmount ?? 0;
                    response = await _cashBalanceService.ProcessDollarPurchaseAsync(account, trade);
                    break;

                default:
                    response = new TradeResponse
                    {
                        Success = false,
                        Message = $"Unsupported trade type: {request.Type}"
                    };
                    break;
            }

            // Add trade to account history
            account.Trades.Add(trade);
            account.LastUpdated = DateTime.Now;

            _logger.LogInformation($"Trade processed: {trade.TradeId}, Status: {trade.Status}, New Cash Balance: ${account.TradeCashBalance:F2}");

            return response;
        }

        public async Task<Account> GetAccountAsync(string accountId)
        {
            await Task.CompletedTask;
            return _accounts.TryGetValue(accountId, out var account) ? account : null;
        }

        public async Task<List<Trade>> GetTradeHistoryAsync(string accountId)
        {
            await Task.CompletedTask;
            var account = await GetAccountAsync(accountId);
            return account?.Trades.OrderByDescending(t => t.TradeTime).ToList() ?? new List<Trade>();
        }

        public async Task<decimal> GetEODPriceAsync(string symbol)
        {
            await Task.CompletedTask;
            return _eodPrices.TryGetValue(symbol, out var price) ? price : 100.00m; // Default price
        }

        public async Task UpdateTradeCashBalanceAsync(string accountId, decimal newBalance)
        {
            await Task.CompletedTask;
            var account = await GetAccountAsync(accountId);
            if (account != null)
            {
                account.TradeCashBalance = newBalance;
                account.LastUpdated = DateTime.Now;
                _logger.LogInformation($"Updated trade cash balance for account {accountId}: ${newBalance:F2}");
            }
        }

        public async Task<List<Account>> GetAllAccountsAsync()
        {
            await Task.CompletedTask;
            return _accounts.Values.ToList();
        }
    }
}