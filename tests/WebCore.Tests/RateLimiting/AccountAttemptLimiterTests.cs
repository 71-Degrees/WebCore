/*
 * ---------------------------------------------------------------------------
 * Copyright © 2026 71 Degrees. All rights reserved.
 * ---------------------------------------------------------------------------
 */

using WebCore.RateLimiting;

namespace WebCore.Tests.RateLimiting;

public class AccountAttemptLimiterTests
{
    [Fact]
    public void One_address_is_stopped_after_ten_attempts_but_other_addresses_are_not()
    {
        var limiter = new AccountAttemptLimiter();

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire("198.51.100.1"));
        }

        Assert.False(limiter.TryAcquire("198.51.100.1"));
        Assert.True(limiter.TryAcquire("198.51.100.2"));
    }

    [Fact]
    public void The_shared_ceiling_defaults_to_twenty_and_can_be_raised()
    {
        var defaults = new AccountAttemptLimiter();
        var raised = new AccountAttemptLimiter(globalMax: 30);

        // distinct addresses, so only the shared ceiling can refuse
        Assert.Equal(20, Enumerable.Range(0, 100).Count(i => defaults.TryAcquire($"192.0.2.{i}")));
        Assert.Equal(30, Enumerable.Range(0, 100).Count(i => raised.TryAcquire($"192.0.2.{i}")));
    }

    [Fact]
    public void The_scale_still_multiplies_a_chosen_shared_ceiling()
    {
        var limiter = new AccountAttemptLimiter(scale: 2, globalMax: 30);

        Assert.Equal(60, Enumerable.Range(0, 100).Count(i => limiter.TryAcquire($"192.0.2.{i}")));
    }

    [Fact]
    public void An_abusive_address_does_not_use_up_the_shared_allowance_real_people_depend_on()
    {
        var limiter = new AccountAttemptLimiter();
        for (var i = 0; i < 200; i++)
        {
            limiter.TryAcquire("203.0.113.66");   // a flood from one address: only its first ten count against the shared ceiling
        }

        // the global ceiling is 20: ten were spent by the flood, so ten more people can still get in
        for (var person = 0; person < 10; person++)
        {
            Assert.True(limiter.TryAcquire($"192.0.2.{person}"), $"person {person} should not be locked out by someone else's flood");
        }
    }

    [Fact]
    public void Password_guesses_against_one_account_are_stopped_whatever_addresses_they_come_from()
    {
        var limiter = new AccountAttemptLimiter();

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquireLogin($"192.0.2.{i}", "Victim@Example.com"));
        }

        Assert.False(limiter.TryAcquireLogin("192.0.2.200", "victim@example.com"));   // same account, new address, different capitals: still the same account
        Assert.True(limiter.TryAcquireLogin("192.0.2.200", "someone-else@example.com"));
    }

    [Fact]
    public void The_code_flow_is_limited_per_address_only_and_never_spends_the_shared_allowance()
    {
        var limiter = new AccountAttemptLimiter();

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquireAddress("198.51.100.5"));
        }

        Assert.False(limiter.TryAcquireAddress("198.51.100.5"));
        for (var person = 0; person < 20; person++)
        {
            Assert.True(limiter.TryAcquire($"192.0.2.{person}"));   // all twenty shared attempts are still there
        }
    }

    [Fact]
    public void A_request_with_no_known_address_is_not_blocked_by_an_address_limit()
    {
        var limiter = new AccountAttemptLimiter();

        for (var i = 0; i < 25; i++)
        {
            Assert.True(limiter.TryAcquireAddress(null));
        }
    }

    [Fact]
    public void A_scale_multiplies_every_ceiling_and_nothing_below_one_lowers_them()
    {
        var scaled = new AccountAttemptLimiter(scale: 3);
        var addressAllowed = Enumerable.Range(0, 40).Count(_ => scaled.TryAcquireAddress("203.0.113.5"));

        var floor = new AccountAttemptLimiter(scale: 0);
        var floorAllowed = Enumerable.Range(0, 40).Count(_ => floor.TryAcquireAddress("203.0.113.5"));

        Assert.Equal(30, addressAllowed);   // 10 per address, three times over
        Assert.Equal(10, floorAllowed);     // a scale under one is treated as one
    }
}
