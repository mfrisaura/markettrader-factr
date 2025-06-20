using Microsoft.AspNetCore.Mvc;
using FIXLinkTradingServer.Models;
using FIXLinkTradingServer.Services;

namespace FIXLinkTradingServer.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TradingController : ControllerBase
    {
        private readonly ITradingService _tradingService;
        private readonly ILogger<TradingController> _logger;

        public TradingController(ITradingService tradingService, ILogger<TradingController> logger)
        {
            _tradingService = tradingService;
            _logger = logger;
        }

        [HttpPost("trade")]
        public async Task<ActionResult<TradeResponse>> SubmitTrade([FromBody] TradeRequest request)
        {
            try
            {
                var response = await _tradingService.ProcessTradeAsync(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing trade");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("account/{accountId}")]
        public async Task<ActionResult<Account>> GetAccount(string accountId)
        {
            var account = await _tradingService.GetAccountAsync(accountId);
            if (account == null)
                return NotFound($"Account {accountId} not found");
            
            return Ok(account);
        }

        [HttpGet("account/{accountId}/trades")]
        public async Task<ActionResult<List<Trade>>> GetTradeHistory(string accountId)
        {
            var trades = await _tradingService.GetTradeHistoryAsync(accountId);
            return Ok(trades);
        }

        [HttpPost("account/{accountId}/cash-balance")]
        public async Task<ActionResult> UpdateCashBalance(string accountId, [FromBody] decimal newBalance)
        {
            await _tradingService.UpdateTradeCashBalanceAsync(accountId, newBalance);
            return Ok(new { message = "Cash balance updated successfully" });
        }

        [HttpGet("accounts")]
        public async Task<ActionResult<List<Account>>> GetAllAccounts()
        {
            var accounts = await _tradingService.GetAllAccountsAsync();
            return Ok(accounts);
        }

        [HttpGet("eod-price/{symbol}")]
        public async Task<ActionResult<decimal>> GetEODPrice(string symbol)
        {
            var price = await _tradingService.GetEODPriceAsync(symbol);
            return Ok(new { symbol, price });
        }
    }
}