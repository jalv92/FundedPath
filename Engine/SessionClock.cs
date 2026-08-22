using System;

namespace FundedPath.Engine
{
    // Trading-day bucketing for a CME futures session. Pure: no NinjaTrader types, no clock reads,
    // no time-zone lookups.
    //
    // Every DateTime crossing this class is ALREADY Eastern time. The NinjaTrader layer converts from
    // Core.Globals.GeneralOptions.TimeZoneInfo to ET before calling in. Doing the conversion here would
    // need a TimeZoneInfo lookup, which is machine state, and would make these functions
    // non-deterministic on a machine missing the zone id.
    public static class SessionClock
    {
        // The CME electronic session opens at 18:00 ET and the day it belongs to is the NEXT calendar
        // date: a fill at Monday 18:30 ET is part of TUESDAY's trading day.
        public static readonly TimeSpan DefaultSessionOpen = new TimeSpan(18, 0, 0);

        // Lucid's drawdown floor ratchets on the session close, never on an intraday high (spec 1.1).
        // 16:45 ET is the moment the dashboard stamps the end-of-day balance the trail reads.
        public static readonly TimeSpan DefaultRatchet = new TimeSpan(16, 45, 0);

        public static DateTime TradingDate(DateTime easternTime)
        {
            return TradingDate(easternTime, DefaultSessionOpen);
        }

        public static DateTime TradingDate(DateTime easternTime, TimeSpan sessionOpen)
        {
            // Guarded rather than clamped: a session open outside one day is a programming error in the
            // caller, not runtime data, and silently repairing it would misfile every fill of the run.
            if (sessionOpen < TimeSpan.Zero || sessionOpen >= TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException("sessionOpen", "sessionOpen must be inside a single day.");

            DateTime date = easternTime.Date;

            // At or after the open the timestamp already belongs to the next calendar date's session.
            if (easternTime.TimeOfDay >= sessionOpen)
                date = date.AddDays(1);

            // The weekend is not a trading day. CME is shut from Friday 17:00 ET until Sunday 18:00 ET,
            // so anything landing on Saturday or Sunday is either the Sunday-evening reopen (which the
            // rule above has already pushed to Monday) or a stale/out-of-hours timestamp. Both belong to
            // Monday. Stepping one day at a time rather than doing the arithmetic so the intent reads:
            // Saturday needs two steps, Sunday one.
            //
            // Market holidays are deliberately NOT modelled. A holiday simply produces a trading day with
            // no fills, and the engine's qualifying-day test already ignores those, so a holiday calendar
            // would buy nothing and would need maintaining every year.
            while (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
                date = date.AddDays(1);

            // Unspecified on purpose: the result is a calendar LABEL, not an instant. Stamping it Local or
            // Utc invites a caller to call ToUniversalTime() on it, which would shift the bucket by a day.
            return DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        }

        public static DateTime RatchetTime(DateTime tradingDate, TimeSpan ratchetAt)
        {
            if (ratchetAt < TimeSpan.Zero || ratchetAt >= TimeSpan.FromDays(1))
                throw new ArgumentOutOfRangeException("ratchetAt", "ratchetAt must be inside a single day.");

            // The ratchet lands on the trading date ITSELF, not on the calendar date the session opened.
            // Tuesday's session opens Monday 18:00 ET and closes Tuesday afternoon, so Tuesday's ratchet
            // is Tuesday 16:45 ET. .Date strips any time already carried on the input.
            return DateTime.SpecifyKind(tradingDate.Date.Add(ratchetAt), DateTimeKind.Unspecified);
        }
    }
}
