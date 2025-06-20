using System.Text;

namespace FIXLinkTradingServer.Models
{
    public enum TradeType
    {
        ShareSell,
        DollarSell,
        SharePurchase,
        DollarPurchase
    }

    public enum TradeStatus
    {
        Pending,
        ExecutedAtMarket,
        CoveredByCash,
        Rejected,
        PartiallyExecuted
    }

    public class Account
    {
        public string AccountId { get; set; }
        public decimal TradeCashBalance { get; set; }
        public decimal StartingTradeCashBalance { get; set; }
        public Dictionary<string, decimal> Positions { get; set; } = new Dictionary<string, decimal>();
        public List<Trade> Trades { get; set; } = new List<Trade>();
        public DateTime LastUpdated { get; set; }
        public decimal CashThreshold { get; set; } = 100.00m;
        public decimal MaxTradeCashUsage { get; set; } = 1000.00m; // PCS provided limit
        public bool ThresholdAlertSent { get; set; }
    }

    public class Trade
    {
        public string TradeId { get; set; } = Guid.NewGuid().ToString();
        public string OrderId { get; set; }
        public string Symbol { get; set; }
        public decimal RequestedQuantity { get; set; }
        public decimal ExecutedQuantity { get; set; }
        public decimal EODPrice { get; set; }
        public decimal ExecutedPrice { get; set; }
        public TradeType Type { get; set; }
        public TradeStatus Status { get; set; }
        public string Side { get; set; } // "1" = Buy, "2" = Sell
        public DateTime TradeTime { get; set; }
        public decimal RequestedValue { get; set; }
        public decimal ExecutedValue { get; set; }
        public decimal CashAdjustment { get; set; }
        public string AccountId { get; set; }
        public bool SentToBNY { get; set; }
        public string Notes { get; set; }
        public decimal CashCovered { get; set; }
    }

    public class TradeRequest
    {
        public string AccountId { get; set; }
        public TradeType Type { get; set; }
        public string Symbol { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? DollarAmount { get; set; }
        public DateTime RequestTime { get; set; } = DateTime.Now;
    }

    public class TradeResponse
    {
        public string TradeId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public decimal? ExecutedQuantity { get; set; }
        public decimal? ExecutedValue { get; set; }
        public decimal? CashCovered { get; set; }
        public decimal? CashAdjustment { get; set; }
        public TradeStatus Status { get; set; }
        public decimal NewCashBalance { get; set; }
    }

    public class FIXMessage
    {
        public Dictionary<int, string> Fields { get; set; } = new Dictionary<int, string>();
        
        public string GetField(int tag) => Fields.TryGetValue(tag, out var value) ? value : null;
        public void SetField(int tag, string value) => Fields[tag] = value;
        
        public string ToFIXString()
        {
            var sb = new StringBuilder();
            foreach (var field in Fields)
            {
                sb.Append($"{field.Key}={field.Value}\x01");
            }
            return sb.ToString();
        }
        
        public static FIXMessage ParseFIXString(string fixString)
        {
            var message = new FIXMessage();
            var fields = fixString.Split('\x01', StringSplitOptions.RemoveEmptyEntries);
            
            foreach (var field in fields)
            {
                var parts = field.Split('=', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int tag))
                {
                    message.SetField(tag, parts[1]);
                }
            }
            return message;
        }
    }
}