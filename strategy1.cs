#region imports
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Globalization;
    using System.Drawing;
    using QuantConnect;
    using QuantConnect.Algorithm.Framework;
    using QuantConnect.Algorithm.Framework.Selection;
    using QuantConnect.Algorithm.Framework.Alphas;
    using QuantConnect.Algorithm.Framework.Portfolio;
    using QuantConnect.Algorithm.Framework.Execution;
    using QuantConnect.Algorithm.Framework.Risk;
    using QuantConnect.Parameters;
    using QuantConnect.Benchmarks;
    using QuantConnect.Brokerages;
    using QuantConnect.Util;
    using QuantConnect.Interfaces;
    using QuantConnect.Algorithm;
    using QuantConnect.Indicators;
    using QuantConnect.Data;
    using QuantConnect.Data.Consolidators;
    using QuantConnect.Data.Custom;
    using QuantConnect.DataSource;
    using QuantConnect.Data.Fundamental;
    using QuantConnect.Data.Market;
    using QuantConnect.Data.UniverseSelection;
    using QuantConnect.Notifications;
    using QuantConnect.Orders;
    using QuantConnect.Orders.Fees;
    using QuantConnect.Orders.Fills;
    using QuantConnect.Orders.Slippage;
    using QuantConnect.Scheduling;
    using QuantConnect.Securities;
    using QuantConnect.Securities.Equity;
    using QuantConnect.Securities.Future;
    using QuantConnect.Securities.Option;
    using QuantConnect.Securities.Forex;
    using QuantConnect.Securities.Crypto;   
    using QuantConnect.Securities.Interfaces;
    using QuantConnect.Storage;
    using QuantConnect.Data.Custom.AlphaStreams;
    using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
    using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
#endregion
namespace QuantConnect.Algorithm.CSharp
{
    public class FormalAsparagusLeopard : QCAlgorithm
    {
        // initialize our changes to nothing
        private SecurityChanges _changes = SecurityChanges.None;
        private const decimal minDollarVolume = 50*1000*1000;
        private const decimal minStockPrice = 10;
        
        // Number of last N bars to calculate return for.
        private const int numberOfLookbackBars = 50;
        
        // For buying, stock must be among top N leaders
        private const int numberOfLeadersForBuying = 1000;

        private const decimal initialPortfolioSize = 100*1000;

        private const int maxNumberOfStocksInPortfolio = 15;

        private const decimal positionSize = initialPortfolioSize / maxNumberOfStocksInPortfolio;

        private const int goodMarketSpyMovingAveragePeriod = 100;

        void InitAlgorithmParameters()
        {
            DualMovingAverageMomentum.SlowPeriod = 50;
            DualMovingAverageMomentum.FastPeriod = 21;
            DualMovingAverageMomentum.MaxAgeOfUptrend = 5;
            DualMovingAverageMomentum.MinAgeOfUptrend = 2;
        }

        void SetTradingSchedule()
        {
            Schedule.On(DateRules.EveryDay(), TimeRules.AfterMarketOpen("SPY"), Liquidate);
            Schedule.On(DateRules.EveryDay(), TimeRules.Noon, TakeNewPositions);
        }

        bool LiquidatePosition(StockData data)
        {
            return data.dualAvgMomentum == decimal.MinusOne;
        }

        IEnumerable<Symbol> IdentifyStocksToBuy(int numberOfStocksToBuy, HashSet<Symbol> existingHoldings)
        {
            IEnumerable<StockData> datas = 
                    EpsCheck(IdentifyLeaders(numberOfLeadersForBuying))
                    .Where(data => !existingHoldings.Contains(data.symbol));

            IEnumerable<StockData> filtered = datas
                    .Where(data => data.dualAvgMomentum.ToBuy());
            
            return filtered
                    .Take(numberOfStocksToBuy)
                    .Select(data => data.symbol);
        }

        private IEnumerable<StockData> EpsCheck(IEnumerable<StockData> stockDatas)
        {
            List<StockDataWithEarningsGrowth> stockDataWithEarningsGrowth = new List<StockDataWithEarningsGrowth>();
            foreach(StockData data in symbolToStockData.Values){
                RollingWindow<decimal> epsHistory = data.epsHistory;
                if(epsHistory.Count == 6 && epsHistory[4] != 0 && epsHistory[5] != 0){
                    decimal q1Growth = (epsHistory[0] - epsHistory[4])/Math.Abs(epsHistory[4]);
                    decimal q2Growth = (epsHistory[1] - epsHistory[5])/Math.Abs(epsHistory[5]);
                    if(q1Growth > 0.2M && q2Growth > 0.05M && q1Growth > q2Growth){
                        stockDataWithEarningsGrowth.Add(new StockDataWithEarningsGrowth(data, q1Growth + q2Growth));
                    }
                }
            }
            return stockDataWithEarningsGrowth
                .OrderByDescending(x => x.EarningsGrowth) // Calculate the performance for each stock
                .Select(x => x.StockData);
        }

        private IEnumerable<StockData> IdentifyLeaders(int numberOfLeaders)
        {
            List<StockDataWithReturn> stockDataWithReturns = new List<StockDataWithReturn>();
            foreach(StockData data in symbolToStockData.Values){
                if(!data.priceHistory.IsReady){
                    continue;
                }
                decimal oldestPrice = data.priceHistory[data.priceHistory.Count-1];
                decimal nDayReturn = (data.Price() - oldestPrice) / oldestPrice;
                stockDataWithReturns.Add(new StockDataWithReturn(data, nDayReturn));
            }
            return stockDataWithReturns
                .OrderByDescending(dataWithReturn =>
                    dataWithReturn.PriceChangeRatio) // Calculate the performance for each stock
                .Take(numberOfLeaders)// Take the top N stocks
                .Select(dataWithReturn => dataWithReturn.StockData);
        }

        private bool GoodMarket()
        {
            if (Securities.ContainsKey("SPY")){
                var spyPrice = Securities["SPY"].Price;
                if(spyMovingAvg.IsReady){
                    Log("Spy Moving Avg ready");
                }
                if(spyMovingAvg.IsReady && spyPrice > spyMovingAvg.Current.Value){
                    Log("Good Market");
                    return true;
                }
            } else {
                Log("SPY is not found.");
            }
            return false;
        }

        class StockData 
        {
            public StockData(Symbol symbol)
            {
                this.symbol = symbol;
            }
            
            public Symbol symbol{ get; }

            public DateTime lastFileDate{ get; set; }

            public void AddPrice(DateTime time, decimal price)
            {
                priceHistory.Add(price);
                dualAvgMomentum.Update(new IndicatorDataPoint(time, price));
            }

            public decimal Price()
            {
                return priceHistory[0];
            }

            public RollingWindow<decimal> priceHistory = new RollingWindow<decimal>(numberOfLookbackBars);

            public RollingWindow<decimal> epsHistory = new RollingWindow<decimal>(6);

            public DualMovingAverageMomentum dualAvgMomentum = new DualMovingAverageMomentum("ten_twenty_momentum");
        }
    
        Dictionary<Symbol, StockData> symbolToStockData = new Dictionary<Symbol, StockData>();
        SimpleMovingAverage spyMovingAvg = new SimpleMovingAverage("spy_sma", goodMarketSpyMovingAveragePeriod);
        HashSet<Symbol> uninterestingSymbols = new HashSet<Symbol>();

        private StockData GetDataForSymbol(Symbol symbol)
        {
            StockData data;
            if(!symbolToStockData.ContainsKey(symbol)){
                data = new StockData(symbol);
                symbolToStockData[symbol] = data;
            } else {
                data = symbolToStockData[symbol];
            }
            return data;
        }

        private IEnumerable<Symbol> CoarseSelectionFunction(IEnumerable<CoarseFundamental> coarse)
        {
            //if (Time.DayOfWeek != DayOfWeek.Monday){
            //    return Universe.Unchanged; // Don't change the universe unless it's Monday
            //}
            List<Symbol> filtered = new List<Symbol>();
            foreach(CoarseFundamental cf in coarse){
                if(cf.Symbol.Value == "SPY"){
                    spyMovingAvg.Update(Time, cf.Price);
                    filtered.Add(cf.Symbol);
                    continue;
                }
                Log(cf.Symbol.Value);
                if(!cf.HasFundamentalData){
                    continue;
                }
                if(uninterestingSymbols.Contains(cf.Symbol)){
                    continue;
                }
                if(!IsInteresting(cf)){
                    uninterestingSymbols.Add(cf.Symbol);
                    if(symbolToStockData.ContainsKey(cf.Symbol)){
                        symbolToStockData.Remove(cf.Symbol);
                    }
                    continue;
                }
                StockData data = GetDataForSymbol(cf.Symbol);
                data.AddPrice(Time, cf.Price);
                filtered.Add(cf.Symbol);
            }
            return filtered;
        }

        public IEnumerable<Symbol> FineSelectionFunction(IEnumerable<FineFundamental> fine)
        { 
            List<Symbol> filtered = new List<Symbol>();
            foreach(FineFundamental fineFundamental in fine){
                if(fineFundamental.Symbol.Value == "SPY"){
                    // Always include SPY in the universe.
                    filtered.Add(fineFundamental.Symbol);
                    continue;
                }
                if(uninterestingSymbols.Contains(fineFundamental.Symbol)){
                    Log("Unexpected. Stock is uninteresting. "+fineFundamental.Symbol);
                    continue;
                }
                StockData data = GetDataForSymbol(fineFundamental.Symbol);
                ProcessFineFundamental(fineFundamental, data);
                filtered.Add(fineFundamental.Symbol);
            }
            return filtered;
        }
        private bool IsInteresting(CoarseFundamental cf)
        {
            return cf.Price > minStockPrice && cf.DollarVolume > minDollarVolume;
        }

        struct StockDataWithReturn
        {
            public StockDataWithReturn(StockData stockData, decimal priceChangeRatio)
            {
                StockData = stockData; 
                PriceChangeRatio = priceChangeRatio;
            }
            public StockData StockData{ get; }
            public decimal PriceChangeRatio{ get; }
        }

        private void ProcessFineFundamental(FineFundamental x, StockData data)
        {
            DateTime lastFileDate = x.FinancialStatements.FileDate;
            if(data.lastFileDate != lastFileDate){
                data.epsHistory.Add(x.EarningReports.BasicEPS.ThreeMonths);
                data.lastFileDate = lastFileDate;
            }
        }

        struct StockDataWithEarningsGrowth
        {
            public StockDataWithEarningsGrowth(StockData stockData, decimal earningsGrowth)
            {
                StockData = stockData; 
                EarningsGrowth = earningsGrowth;
            }
            public StockData StockData{ get; }
            public decimal EarningsGrowth{ get; }
        }

        public override void Initialize()
        {
            InitAlgorithmParameters();

            SetStartDate(2021, 1, 1);
            SetCash(initialPortfolioSize);
            
            UniverseSettings.Resolution = Resolution.Daily;
            AddUniverse(CoarseSelectionFunction, FineSelectionFunction);

            //Schedule.On(DateRules.MonthEnd(), TimeRules.Noon, Liquidate);
            //Schedule.On(DateRules.MonthStart(), TimeRules.Noon, TakeNewPositions);

            AddEquity("SPY");

            SetTradingSchedule();
        }

        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// Slice object keyed by symbol containing the stock data
        public override void OnData(Slice data)
        {
        }

        private void Liquidate()
        {
            foreach(var x in Portfolio.Values){
                if(x.Invested){   
                    StockData data = GetDataForSymbol(x.Symbol);
                    if(LiquidatePosition(data)){
                        Liquidate(x.Symbol);
                        Log("Liquidate " + x.Symbol);
                    }
                }
            }
        }

        private void TakeNewPositions()
        {
            if(!GoodMarket()){
                return;
            }
            int numberOfStocksToBuy = (int) (Portfolio.Cash / positionSize);

            if(numberOfStocksToBuy == 0){
                Log("Less cash (" + Portfolio.Cash + ") than position size.");
                return;
            }

            // Buy stocks that are leaders and that have CANSLIM EPS growth.
            HashSet<Symbol> existingHoldings = new HashSet<Symbol>();
            foreach(var x in Portfolio.Values){
                if(x.Invested){
                    existingHoldings.Add(x.Symbol);
                }
            }
            
            IEnumerable<Symbol> symbolsToBuy = IdentifyStocksToBuy(numberOfStocksToBuy, existingHoldings);
                
            decimal fractionOfPortfolio = positionSize / Portfolio.TotalPortfolioValue;
            foreach(var symbol in symbolsToBuy){
                SetHoldings(symbol, fractionOfPortfolio);
            }
        }

        // Set zero-commission. 
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            _changes = changes;

            /*
            if (changes.AddedSecurities.Count > 0)
            {
                Debug("Securities added: " + string.Join(",", changes.AddedSecurities.Select(x => x.Symbol.Value)));
            }
            if (changes.RemovedSecurities.Count > 0)
            {
                Debug("Securities removed: " + string.Join(",", changes.RemovedSecurities.Select(x => x.Symbol.Value)));
            }
            */
            foreach (var security in changes.AddedSecurities)
            {
                security.FeeModel = new ZeroFeeModel(); // Apply zero fee model to newly added securities
            }
        }

        Dictionary<Symbol,List<TradeReport>> tradeReports = new Dictionary<Symbol,List<TradeReport>>();

        public class TradeReport
        {
            public Symbol Symbol { get; set; }
            public DateTime EntryTime { get; set; }
            public DateTime ExitTime { get; set; }
            public decimal EntryPrice { get; set; }
            public decimal ExitPrice { get; set; }
            public decimal PercentagePnL { get; set; }
        }

        // Log the trades.
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            // Filter only filled orders
            if (orderEvent.Status != OrderStatus.Filled)
                return;

            DateTime currentTime = this.Time;

            if (orderEvent.Direction == OrderDirection.Buy) {
                List<TradeReport> reports; 
                if(tradeReports.ContainsKey(orderEvent.Symbol)){
                    reports = tradeReports[orderEvent.Symbol];
                } else {
                    reports = new List<TradeReport>();
                    tradeReports[orderEvent.Symbol] = reports;
                }
                reports.Add(new TradeReport 
                {
                    Symbol = orderEvent.Symbol,
                    EntryTime = currentTime,
                    EntryPrice = orderEvent.FillPrice
                });
            } else if (orderEvent.Direction == OrderDirection.Sell) {
                List<TradeReport> reports = tradeReports[orderEvent.Symbol];
                TradeReport tradeReport = reports[reports.Count-1];
                tradeReport.ExitTime = currentTime;
                tradeReport.ExitPrice = orderEvent.FillPrice;
                tradeReport.PercentagePnL = ((tradeReport.ExitPrice - tradeReport.EntryPrice)/tradeReport.EntryPrice) * 100;
            }
        }

        // Print stats such as trade details, P/L.
        public override void OnEndOfAlgorithm()
        {
            Debug("Trade Report:");
            List<TradeReport> allTradeReports = new List<TradeReport>();
            foreach(List<TradeReport> reports in tradeReports.Values){ 
                allTradeReports.AddRange(reports);
            }
            IEnumerable<TradeReport> tradeHistory = allTradeReports
                .OrderByDescending(tradeReport => tradeReport.EntryTime);
            foreach(var trade in tradeHistory){
                Debug($"Symbol: {trade.Symbol}, Entry Time: {trade.EntryTime}, Exit Time: {trade.ExitTime}, Percentage PnL: {trade.PercentagePnL:F2}%");
            }
        }
    }

    public class DualMovingAverageMomentum : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider 
    {
        private SimpleMovingAverage _fastSma;
        private SimpleMovingAverage _slowSma;
        private int _barsSinceFastCrossedAboveSlow = 0;
        public static int MinAgeOfUptrend { get; set; }
        public static int MaxAgeOfUptrend { get; set; }
        public static int FastPeriod { get; set; }
        public static int SlowPeriod { get; set; }
      
        public DualMovingAverageMomentum(string name) : base(name)
        {
            _fastSma = new SimpleMovingAverage(name + "_fast", FastPeriod);
            _slowSma = new SimpleMovingAverage(name+"_slow", SlowPeriod);
            WarmUpPeriod = SlowPeriod;
        }

        public override bool IsReady => _fastSma.IsReady && _slowSma.IsReady;

        public int WarmUpPeriod { get; }

        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if(!IsReady){
                _fastSma.Update(input);
                _slowSma.Update(input);
                return decimal.MinusOne;
            }

            decimal lastFastSma = _fastSma;
            decimal lastSlowSma = _slowSma;

            _fastSma.Update(input);
            _slowSma.Update(input);
        
            if(_fastSma > _slowSma){
                if(lastFastSma <= lastSlowSma){
                    _barsSinceFastCrossedAboveSlow = 1;
                } else {
                    _barsSinceFastCrossedAboveSlow += 1;
                }
            } else {
                _barsSinceFastCrossedAboveSlow = 0;
            }

            // Returns true if fast is above slow
            if(_fastSma > lastFastSma) {
                if(_slowSma > lastSlowSma){
                    // both trending up
                    return decimal.One;
                } else {
                    // Only slow trending up
                    return decimal.Zero;
                }  
            } else {
                return decimal.MinusOne;
            }
        }
        
        public bool ToBuy()
        {
            return MinAgeOfUptrend < _barsSinceFastCrossedAboveSlow 
                && _barsSinceFastCrossedAboveSlow < MaxAgeOfUptrend 
                && this.Current.Value == decimal.One;
        }
    }

    public class ZeroFeeModel : IFeeModel
    {
        public OrderFee GetOrderFee(OrderFeeParameters parameters)
        {
            return new OrderFee(new CashAmount(0m, "USD")); // No fees
        }
    }
}
