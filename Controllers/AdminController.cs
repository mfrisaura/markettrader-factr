using Microsoft.AspNetCore.Mvc;
using FIXLinkTradingServer.Services;

namespace FIXLinkTradingServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly IFIXLinkService _fixLinkService;
        private readonly ITradingService _tradingService;
        private readonly ILogger<AdminController> _logger;

        public AdminController(IFIXLinkService fixLinkService, ITradingService tradingService, ILogger<AdminController> logger)
        {
            _fixLinkService = fixLinkService;
            _tradingService = tradingService;
            _logger = logger;
        }

        [HttpPost("connect")]
        public async Task<ActionResult> Connect()
        {
            var success = await _fixLinkService.ConnectAsync();
            return Ok(new { connected = success });
        }

        [HttpPost("disconnect")]
        public async Task<ActionResult> Disconnect()
        {
            await _fixLinkService.DisconnectAsync();
            return Ok(new { message = "Disconnected" });
        }

        [HttpGet("status")]
        public ActionResult GetStatus()
        {
            return Ok(new { connected = _fixLinkService.IsConnected });
        }

        [HttpGet("accounts/summary")]
        public async Task<ActionResult> GetAccountsSummary()
        {
            var accounts = await _tradingService.GetAllAccountsAsync();
            var summary = accounts.Select(a => new
            {
                accountId = a.AccountId,
                tradeCashBalance = a.TradeCashBalance,
                startingBalance = a.StartingTradeCashBalance,
                tradeCount = a.Trades.Count,
                lastActivity = a.LastUpdated,
                belowThreshold = a.TradeCashBalance <= a.CashThreshold
            });
            
            return Ok(summary);
        }
    }
}