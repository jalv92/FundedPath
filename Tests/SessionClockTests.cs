using System;
using FundedPath.Engine;
using Xunit;

// Trading-day bucketing. Every DateTime crossing SessionClock is ALREADY Eastern time -- the NT
// layer converts before calling in -- so these tests deal in plain ET wall clock.
//
// Two boundaries live in this file and they are NOT the same boundary:
//   18:00 ET  the session open, which is where the trading DAY changes;
//   16:45 ET  the ratchet, which is only where the end-of-day BALANCE is stamped.
// Confusing the second for the first misfiles every afternoon fill, which moves closing balances,
// which moves the drawdown floor. That is why the 16:44/16:46 case is asserted explicitly.
public class SessionClockTests
{
    [Fact]
    public void Seventeen_fiftynine_and_eighteen_ohone_are_different_trading_days()
    {
        // Wednesday 2026-08-19. Same calendar date, two minutes apart, opposite sides of the open.
        DateTime beforeOpen = new DateTime(2026, 8, 19, 17, 59, 0);
        DateTime afterOpen  = new DateTime(2026, 8, 19, 18, 1, 0);

        Assert.Equal(new DateTime(2026, 8, 19), SessionClock.TradingDate(beforeOpen));
        Assert.Equal(new DateTime(2026, 8, 20), SessionClock.TradingDate(afterOpen));
        Assert.NotEqual(SessionClock.TradingDate(beforeOpen), SessionClock.TradingDate(afterOpen));

        // 18:00:00 itself belongs to the new day: the comparison is >=, not >.
        Assert.Equal(new DateTime(2026, 8, 20), SessionClock.TradingDate(new DateTime(2026, 8, 19, 18, 0, 0)));
    }

    [Fact]
    public void The_ratchet_time_is_not_the_day_boundary()
    {
        // Two fills straddling 16:45 ET on Wednesday 2026-08-19. Both are the SAME trading day: the
        // ratchet stamps the closing balance, it does not open a new session. A bucketing routine
        // that used 16:45 as the boundary would split one day's P&L across two rows and hand the
        // trailing floor a high-water mark that never existed.
        DateTime beforeRatchet = new DateTime(2026, 8, 19, 16, 44, 0);
        DateTime afterRatchet  = new DateTime(2026, 8, 19, 16, 46, 0);

        Assert.Equal(new DateTime(2026, 8, 19), SessionClock.TradingDate(beforeRatchet));
        Assert.Equal(new DateTime(2026, 8, 19), SessionClock.TradingDate(afterRatchet));
        Assert.Equal(SessionClock.TradingDate(beforeRatchet), SessionClock.TradingDate(afterRatchet));

        Assert.Equal(new TimeSpan(16, 45, 0), SessionClock.DefaultRatchet);
        Assert.Equal(new TimeSpan(18, 0, 0), SessionClock.DefaultSessionOpen);
    }

    [Fact]
    public void Friday_evening_rolls_to_monday()
    {
        // Friday 2026-08-21. The 18:00 reopen would land on Saturday, but CME is shut from Friday
        // 17:00 ET until Sunday 18:00 ET, so the next session that exists is Monday's.
        DateTime fridayEvening = new DateTime(2026, 8, 21, 18, 30, 0);
        Assert.Equal(DayOfWeek.Friday, fridayEvening.DayOfWeek);
        Assert.Equal(new DateTime(2026, 8, 24), SessionClock.TradingDate(fridayEvening));
        Assert.Equal(DayOfWeek.Monday, SessionClock.TradingDate(fridayEvening).DayOfWeek);

        // Friday daytime is still Friday -- the roll is the evening reopen, not the whole day.
        Assert.Equal(new DateTime(2026, 8, 21), SessionClock.TradingDate(new DateTime(2026, 8, 21, 14, 0, 0)));
    }

    [Fact]
    public void Weekend_timestamps_all_land_on_monday()
    {
        // Saturday needs two steps forward, Sunday one; the Sunday 18:00 reopen has already been
        // pushed to Monday by the session-open rule before the weekend loop ever sees it.
        Assert.Equal(new DateTime(2026, 8, 24), SessionClock.TradingDate(new DateTime(2026, 8, 22, 10, 0, 0)));
        Assert.Equal(new DateTime(2026, 8, 24), SessionClock.TradingDate(new DateTime(2026, 8, 23, 12, 0, 0)));
        Assert.Equal(new DateTime(2026, 8, 24), SessionClock.TradingDate(new DateTime(2026, 8, 23, 18, 30, 0)));
    }

    [Fact]
    public void A_trading_date_is_a_calendar_label_not_an_instant()
    {
        // Unspecified on purpose: a caller who found Local or Utc on it could reasonably call
        // ToUniversalTime(), which would shift the bucket by a day and silently re-file the fills.
        DateTime d = SessionClock.TradingDate(new DateTime(2026, 8, 19, 9, 30, 0, DateTimeKind.Local));
        Assert.Equal(DateTimeKind.Unspecified, d.Kind);
        Assert.Equal(TimeSpan.Zero, d.TimeOfDay);
    }

    [Fact]
    public void Ratchet_time_lands_on_the_trading_date_itself()
    {
        // Tuesday's session opens Monday 18:00 ET and closes Tuesday afternoon, so Tuesday's ratchet
        // is Tuesday 16:45 -- the trading date, never the calendar date the session opened on.
        DateTime tradingDate = SessionClock.TradingDate(new DateTime(2026, 8, 17, 18, 30, 0));
        Assert.Equal(new DateTime(2026, 8, 18), tradingDate);

        DateTime ratchet = SessionClock.RatchetTime(tradingDate, SessionClock.DefaultRatchet);
        Assert.Equal(new DateTime(2026, 8, 18, 16, 45, 0), ratchet);
        Assert.Equal(DateTimeKind.Unspecified, ratchet.Kind);

        // Any time already carried on the input is stripped, not added to.
        Assert.Equal(ratchet, SessionClock.RatchetTime(new DateTime(2026, 8, 18, 3, 15, 0), SessionClock.DefaultRatchet));
    }

    [Fact]
    public void A_session_open_or_ratchet_outside_one_day_is_rejected()
    {
        // Guarded rather than clamped: this is a programming error in the caller, and silently
        // repairing it would misfile every fill of the run behind a plausible-looking result.
        DateTime t = new DateTime(2026, 8, 19, 12, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionClock.TradingDate(t, TimeSpan.FromHours(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionClock.TradingDate(t, TimeSpan.FromHours(24)));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionClock.RatchetTime(t, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void A_custom_session_open_moves_the_boundary_with_it()
    {
        // The 18:00 default is CME's; the parameter exists so a different session can be bucketed
        // without a second copy of the weekend logic.
        TimeSpan open = new TimeSpan(17, 0, 0);
        Assert.Equal(new DateTime(2026, 8, 19), SessionClock.TradingDate(new DateTime(2026, 8, 19, 16, 59, 0), open));
        Assert.Equal(new DateTime(2026, 8, 20), SessionClock.TradingDate(new DateTime(2026, 8, 19, 17, 1, 0), open));
    }
}
