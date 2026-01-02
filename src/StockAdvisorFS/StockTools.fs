module StockAdvisorFS.StockTools

open AgentNet

// ============================================================================
// PURE F# FUNCTIONS - Easy to test, compose, and reuse
// XML doc comments become the AI's tool descriptions automatically!
// ============================================================================

/// Gets current stock information including price, change, and basic metrics
let getStockInfo (symbol: string) : string =
    // Simulated data - in production, call a real API
    let stocks =
        dict [
            "AAPL", (178.50m, 2.35m, 28.5m, 2.8m)
            "MSFT", (378.25m, -1.20m, 35.2m, 2.9m)
            "GOOGL", (141.80m, 0.95m, 24.8m, 1.8m)
            "AMZN", (178.90m, 3.10m, 62.5m, 1.9m)
            "TSLA", (248.50m, -5.20m, 72.3m, 0.8m)
        ]

    match stocks.TryGetValue(symbol.ToUpper()) with
    | true, (price, change, pe, marketCap) ->
        $"""Stock: {symbol.ToUpper()}
Price: ${price}
Change: {if change >= 0m then "+" else ""}{change} ({change / price * 100m:F2}%%)
P/E Ratio: {pe}
Market Cap: ${marketCap}T"""
    | false, _ ->
        $"Stock symbol '{symbol}' not found."

/// Gets historical price data for a stock
let getHistoricalPrices (symbol: string) (days: int) : string =
    let days = min days 30
    let basePrice =
        match symbol.ToUpper() with
        | "AAPL" -> 175.0m
        | "MSFT" -> 375.0m
        | "GOOGL" -> 140.0m
        | _ -> 100.0m

    let random = System.Random(symbol.GetHashCode())
    let prices =
        [ for i in days .. -1 .. 0 do
            let date = System.DateTime.Today.AddDays(float -i)
            let variance = decimal (random.NextDouble() * 10.0 - 5.0)
            let dateStr = date.ToString("yyyy-MM-dd")
            let priceStr = (basePrice + variance).ToString("F2")
            yield $"  {dateStr}: ${priceStr}" ]

    let pricesStr = String.concat "\n" prices
    $"Historical prices for {symbol.ToUpper()} (last {days} days):\n{pricesStr}"

/// Calculates volatility metrics for a stock
let calculateVolatility (symbol: string) : string =
    let volatilities =
        dict [
            "AAPL", (1.2m, 22.5m, "Moderate")
            "MSFT", (1.0m, 19.8m, "Low-Moderate")
            "GOOGL", (1.4m, 26.3m, "Moderate")
            "AMZN", (1.6m, 30.1m, "Moderate-High")
            "TSLA", (3.2m, 60.5m, "High")
        ]

    match volatilities.TryGetValue(symbol.ToUpper()) with
    | true, (daily, annual, rating) ->
        $"""Volatility Analysis for {symbol.ToUpper()}:
Daily Volatility: {daily}%%
Annualized Volatility: {annual}%%
Risk Rating: {rating}"""
    | false, _ ->
        $"Volatility data not available for '{symbol}'."

/// Compares fundamental metrics between two stocks
let compareStocks (symbol1: string) (symbol2: string) : string =
    $"""Comparison: {symbol1.ToUpper()} vs {symbol2.ToUpper()}

{symbol1.ToUpper()}:
- P/E Ratio: {if symbol1.ToUpper() = "AAPL" then "28.5" else "35.2"}
- Revenue Growth: {if symbol1.ToUpper() = "AAPL" then "8.2%" else "12.5%"}
- Profit Margin: {if symbol1.ToUpper() = "AAPL" then "25.3%" else "36.7%"}

{symbol2.ToUpper()}:
- P/E Ratio: {if symbol2.ToUpper() = "AAPL" then "28.5" else "35.2"}
- Revenue Growth: {if symbol2.ToUpper() = "AAPL" then "8.2%" else "12.5%"}
- Profit Margin: {if symbol2.ToUpper() = "AAPL" then "25.3%" else "36.7%"}"""


// ============================================================================
// TOOL REGISTRATION - Descriptions extracted from XML docs automatically!
// ============================================================================

let stockInfoTool = Tool.createWithDocs <@ getStockInfo @>
let historicalTool = Tool.createWithDocs <@ getHistoricalPrices @>
let volatilityTool = Tool.createWithDocs <@ calculateVolatility @>
let compareTool = Tool.createWithDocs <@ compareStocks @>
