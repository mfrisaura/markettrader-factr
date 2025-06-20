using Microsoft.AspNetCore.SignalR;
using FIXLinkTradingServer.Services;

namespace FIXLinkTradingServer.Hubs
{
    public class TradingHub : Hub
    {
        private readonly ITradingService _tradingService;
        private readonly ILogger<TradingHub> _logger;

        public TradingHub(ITradingService tradingService, ILogger<TradingHub> logger)
        {
            _tradingService = tradingService;
            _logger = logger;
        }

        public async Task JoinAccountGroup(string accountId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"Account_{accountId}");
        }

        public async Task LeaveAccountGroup(string accountId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"Account_{accountId}");
        }

        public async Task GetAccountStatus(string accountId)
        {
            var account = await _tradingService.GetAccountAsync(accountId);
            if (account != null)
            {
                await Clients.Caller.SendAsync("AccountUpdate", new
                {
                    accountId = account.AccountId,
                    tradeCashBalance = account.TradeCashBalance,
                    lastUpdated = account.LastUpdated,
                    tradeCount = account.Trades.Count
                });
            }
        }

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation($"Client connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            _logger.LogInformation($"Client disconnected: {Context.ConnectionId}");
            await base.OnDisconnectedAsync(exception);
        }
    }
}