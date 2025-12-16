using System.ComponentModel;

namespace StockAdvisorCS;

/// <summary>
/// Stock analysis tools for the StockAdvisor agent.
/// Each method decorated with [Description] becomes available to the AI via AIFunctionFactory.
/// </summary>
public static class StockTools
{
    [Description("Gets current stock information including price, change, and basic metrics for a given ticker symbol")]
    public static string GetStockInfo(
        [Description("The stock ticker symbol (e.g., AAPL, MSFT, GOOGL)")] string symbol)
    {
        // Simulated stock data - in production, this would call a real API
        var stocks = new Dictionary<string, (decimal price, decimal change, decimal pe, decimal marketCap)>
        {
            ["AAPL"] = (178.50m, 2.35m, 28.5m, 2.8m),
            ["MSFT"] = (378.25m, -1.20m, 35.2m, 2.9m),
            ["GOOGL"] = (141.80m, 0.95m, 24.8m, 1.8m),
            ["AMZN"] = (178.90m, 3.10m, 62.5m, 1.9m),
            ["TSLA"] = (248.50m, -5.20m, 72.3m, 0.8m)
        };

        if (stocks.TryGetValue(symbol.ToUpper(), out var data))
        {
            return $"""
                Stock: {symbol.ToUpper()}
                Price: ${data.price}
                Change: {(data.change >= 0 ? "+" : "")}{data.change} ({data.change / data.price * 100:F2}%)
                P/E Ratio: {data.pe}
                Market Cap: ${data.marketCap}T
                """;
        }

        return $"Stock symbol '{symbol}' not found in database.";
    }

    [Description("Gets historical price data for a stock over a specified number of days")]
    public static string GetHistoricalPrices(
        [Description("The stock ticker symbol")] string symbol,
        [Description("Number of days of historical data to retrieve (max 30)")] int days)
    {
        days = Math.Min(days, 30);

        // Simulated historical data
        var random = new Random(symbol.GetHashCode());
        var basePrice = symbol.ToUpper() switch
        {
            "AAPL" => 175.0m,
            "MSFT" => 375.0m,
            "GOOGL" => 140.0m,
            _ => 100.0m
        };

        var prices = new List<string>();
        for (int i = days; i >= 0; i--)
        {
            var date = DateTime.Today.AddDays(-i);
            var variance = (decimal)(random.NextDouble() * 10 - 5);
            var price = basePrice + variance;
            prices.Add($"  {date:yyyy-MM-dd}: ${price:F2}");
        }

        return $"Historical prices for {symbol.ToUpper()} (last {days} days):\n{string.Join("\n", prices)}";
    }

    [Description("Calculates volatility metrics for a stock based on recent price movements")]
    public static string CalculateVolatility(
        [Description("The stock ticker symbol")] string symbol)
    {
        // Simulated volatility data
        var volatilities = new Dictionary<string, (decimal daily, decimal annual, string rating)>
        {
            ["AAPL"] = (1.2m, 22.5m, "Moderate"),
            ["MSFT"] = (1.0m, 19.8m, "Low-Moderate"),
            ["GOOGL"] = (1.4m, 26.3m, "Moderate"),
            ["AMZN"] = (1.6m, 30.1m, "Moderate-High"),
            ["TSLA"] = (3.2m, 60.5m, "High")
        };

        if (volatilities.TryGetValue(symbol.ToUpper(), out var data))
        {
            return $"""
                Volatility Analysis for {symbol.ToUpper()}:
                Daily Volatility: {data.daily}%
                Annualized Volatility: {data.annual}%
                Risk Rating: {data.rating}
                """;
        }

        return $"Volatility data not available for '{symbol}'.";
    }

    [Description("Compares fundamental metrics between two stocks to help with investment decisions")]
    public static string CompareStocks(
        [Description("First stock ticker symbol")] string symbol1,
        [Description("Second stock ticker symbol")] string symbol2)
    {
        return $"""
            Comparison: {symbol1.ToUpper()} vs {symbol2.ToUpper()}

            {symbol1.ToUpper()}:
            - P/E Ratio: {(symbol1.ToUpper() == "AAPL" ? "28.5" : "35.2")}
            - Revenue Growth: {(symbol1.ToUpper() == "AAPL" ? "8.2%" : "12.5%")}
            - Profit Margin: {(symbol1.ToUpper() == "AAPL" ? "25.3%" : "36.7%")}
            - Debt/Equity: {(symbol1.ToUpper() == "AAPL" ? "1.8" : "0.5")}

            {symbol2.ToUpper()}:
            - P/E Ratio: {(symbol2.ToUpper() == "AAPL" ? "28.5" : "35.2")}
            - Revenue Growth: {(symbol2.ToUpper() == "AAPL" ? "8.2%" : "12.5%")}
            - Profit Margin: {(symbol2.ToUpper() == "AAPL" ? "25.3%" : "36.7%")}
            - Debt/Equity: {(symbol2.ToUpper() == "AAPL" ? "1.8" : "0.5")}
            """;
    }
}
