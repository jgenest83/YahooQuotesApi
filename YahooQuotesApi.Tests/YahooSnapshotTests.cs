using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace YahooQuotesApi.Tests
{
    public class YahooSnapshotTest
    {
        private readonly Action<string> Write;
        public YahooSnapshotTest(ITestOutputHelper output) => Write = output.WriteLine;

        [Fact]
        public async Task SimpleQuery()
        {
            // one symbol
            Security? security = await new YahooSnapshot().GetAsync("C");
            if (security == null)
                throw new NullReferenceException("Invalid Symbol: C");
            Assert.True(security.RegularMarketPrice > 0);

            // multiple symbols
            Dictionary<string, Security?> securities = await new YahooSnapshot().GetAsync(new[] { "C", "MSFT" });
            Assert.Equal(2, securities.Count);
            Security? msft = securities["MSFT"];
            if (msft == null)
                throw new NullReferenceException("Invalid Symbol: MSFT");
            Assert.True(msft.RegularMarketVolume > 0);
        }

        [Fact]
        public async Task BadSymbol()
        {
            Security? security = await new YahooSnapshot().GetAsync("InvalidSymbol");
            Assert.Null(security);
        }

        [Fact]
        public async Task BadSymbols()
        {
            Dictionary<string, Security?> securities = await new YahooSnapshot().GetAsync(new[] { "MSFT", "InvalidSymbol" });
            Assert.Equal(2, securities.Count);
            Security? msft = securities["MSFT"];
            if (msft == null)
                throw new NullReferenceException("Invalid Symbol: MSFT");
            Assert.Null(securities["InvalidSymbol"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("", "")]
        [InlineData("C", "A", "C ")]
        public async Task TestSymbolArgument(params string[] symbols)
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await new YahooSnapshot().GetAsync(symbols));
        }

        [Fact]
        public async Task TestEmptyList()
        {
            Assert.Empty(await new YahooSnapshot().GetAsync(new List<string>()));
        }

        [Fact]
        public async Task TestFieldsArgument()
        {
            Security? security = await new YahooSnapshot()
                // can use string field names:
                .Fields("Bid", "Ask", "Tradeable", "LongName")
                // and/or field enums:
                .Fields(Field.RegularMarketPrice, Field.Currency)
                .GetAsync("C");

            // when no fields are specified, many(all?) fields are returned!
            security = await new YahooSnapshot().GetAsync("C") ?? throw new Exception("Invalid symbol: C");
            Assert.True(security.Fields.Count > 10);

            // duplicate field
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await new YahooSnapshot().Fields("bid", "currency", "bid").GetAsync("C"));

            // duplicate field
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await new YahooSnapshot().Fields("currency", "bid").Fields(Field.Ask, Field.Bid).GetAsync("C"));

            // invalid fields return null
            security = await new YahooSnapshot().Fields("invalidfield").GetAsync("C") ?? throw new Exception("Invalid symbol: C");
            Assert.Null(security["invalidfield"]);
        }

        [Fact]
        public async Task TestQuery()
        {
            Security security = await new YahooSnapshot().GetAsync("AAPL") ?? throw new Exception("bad symbol");

            // String or enum indexers return dynamic type.
            //security.Fields.TryGetValue("Bid", out dynamic? bid2);
            //bid2 = security.Fields["Bid"];
            //bid2 = security["Bid"];
            //bid2 = security[Field.Bid];

            Assert.Equal("Apple Inc.", security["LongName"]);
        }

        [Fact]
        public async Task TestManySymbols()
        {
            List<string> symbols = File.ReadAllLines(@"..\..\..\symbols.txt")
                .Where(line => !line.StartsWith("#"))
                .Take(100)
                .ToList();

            Dictionary<string, Security?> securities = await new YahooSnapshot().GetAsync(symbols);

            var counted = symbols.Where(s => securities.ContainsKey(s)).Count();
            Write($"requested symbols: {symbols.Count}");
            Write($"returned securities: {securities.Count}");
            Write($"missing: {symbols.Count - counted}");
        }

        [Fact]
        public async Task TestDateTime()
        {
            Security security = await new YahooSnapshot().GetAsync("C") ?? throw new Exception("Invalid symbol");

            // RegularMarketTime is the time of the last price during regular market hours.
            long seconds = security.RegularMarketTime ?? throw new Exception("Invalid seconds");

            Write("UnixTimeSeconds: " + seconds);

            Instant instant = Instant.FromUnixTimeSeconds(seconds);
            Write("Instant: " + instant); // ISO 8601 format string: yyyy-MM-ddTHH:mm:ssZ

            string? exchangeTimezoneName = security.ExchangeTimezoneName;
            Write("ExchangeTimezoneName: " + exchangeTimezoneName);

            DateTimeZone? timeZone = DateTimeZoneProviders.Tzdb.GetZoneOrNull(exchangeTimezoneName);
            Assert.NotNull(timeZone);

            ZonedDateTime zdt = instant.InZone(timeZone);
            Write("ZonedDateTime: " + zdt);

            // Using the provided extension methods:
            timeZone = security.ExchangeTimezoneName?.ToTimeZone() ?? throw new TimeZoneNotFoundException(security.ExchangeTimezoneName);

            ZonedDateTime? zdt2 = security.RegularMarketTime?.ToZonedDateTime(timeZone);
            Assert.Equal(zdt, zdt2);
        }

        [Theory]
        [InlineData("GMEXICOB.MX")] // Mexico City - 6:00
        [InlineData("TD.TO")]  // Canada -5
        [InlineData("SPY")]    // USA -5
        [InlineData("PETR4.SA")] //Sao_Paulo -3
        [InlineData("BP.L")]   // London 0:
        [InlineData("AIR.PA")] // Paris +1
        [InlineData("AIR.DE")] // Xetra +1
        [InlineData("AGL.JO")] //Johannesburg +2
        [InlineData("AFLT.ME")] // Moscow +3:00
        [InlineData("UNITECH.BO")] // IST +5:30
        [InlineData("2800.HK")] // Hong Kong +8
        [InlineData("000001.SS")] // Shanghai +8
        [InlineData("2448.TW")] // Taiwan +8
        [InlineData("005930.KS")] // Seoul +9
        [InlineData("7203.T")] // Tokyo +9 (Toyota)
        [InlineData("NAB.AX")] // Sydney +10
        [InlineData("FBU.NZ")] // Auckland + 12
        public async Task TestInternationalStocks(string symbol)
        {
            Security security = await new YahooSnapshot().GetAsync(symbol) ?? throw new Exception("invalid symbol: " + symbol);
            DateTimeZone timeZone = security.ExchangeTimezoneName?.ToTimeZoneOrNull() ?? throw new TimeZoneNotFoundException(security.ExchangeTimezoneName);
            ZonedDateTime? zdt = security.RegularMarketTime?.ToZonedDateTime(timeZone);

            Write($"Symbol:        {symbol}");
            Write($"TimeZone:      {timeZone.Id}");
            Write($"ZonedDateTime: {zdt}");
        }

        [Fact]
        public async Task TestLoggerInjection()
        {
            YahooSnapshot yahooQuotes = new ServiceCollection()
                .AddSingleton<YahooSnapshot>()
                .BuildServiceProvider()
                .GetRequiredService<YahooSnapshot>();

            await yahooQuotes.GetAsync("C"); // log message should appear in the debug output (when debugging)
        }
    }
}
