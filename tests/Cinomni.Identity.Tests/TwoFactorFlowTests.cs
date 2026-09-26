using Cinomni.Identity.Application;
using Cinomni.Identity.Contracts;
using Cinomni.Identity.Persistence;
using Cinomni.Identity.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinomni.Identity.Tests;

/// <summary>
/// The second factor end to end against a real PostgreSQL: enrolling it, spending it, getting back in
/// without it, and taking it off. The scenarios that matter here are the ones where a mistake locks
/// somebody out of their own installation, which on a self-hosted single-administrator system is a
/// worse outcome than most of what the factor defends against.
/// </summary>
[Collection(IdentitySecretsCollection.Serial)]
public sealed class TwoFactorFlowTests : IAsyncLifetime
{
    /// <summary>32 fixed bytes. A test key, never a real one, and it never leaves this file.</summary>
    private const string MasterKeyBase64 = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";

    private const string Password = "correct-horse";

    private string? _previousKey;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _previousKey = Environment.GetEnvironmentVariable("CINOMNI_SECRET_KEY");
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", MasterKeyBase64);
        _provider = await IdentityTestHost.CreateAsync("cinomni_test_identity_two_factor");
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", _previousKey);
    }

    [Fact]
    public async Task Enrolling_does_not_take_effect_until_a_code_confirms_it()
    {
        var userId = await CreateAdminAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);

            Assert.True(enrollment.IsSuccess, enrollment.IsSuccess ? null : enrollment.Error.Message);
            Assert.Contains("otpauth://totp/Cinomni%3Aadmin?", enrollment.Value.EnrollmentUri, StringComparison.Ordinal);

            // The secret is stored and the account still signs in exactly as before. Somebody who
            // opened the enrollment screen and closed it must not be locked out by a QR code they
            // never scanned.
            Assert.False(await twoFactor.IsEnabledAsync(userId));
        }

        // And the secret that was stored is not readable from the row.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
                .Users.SingleAsync(u => u.Id == userId.Value);

            Assert.NotNull(user.TotpSecretCipher);
            Assert.NotNull(user.TotpSecretKeyId);
            Assert.Null(user.TotpConfirmedAt);
        }
    }

    [Fact]
    public async Task A_confirmed_factor_gates_the_sign_in_and_a_code_completes_it()
    {
        var userId = await CreateAdminAsync();
        var secret = await EnrollAsync(userId);

        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var authenticator = scope.ServiceProvider.GetRequiredService<IAuthenticator>();

        // The password alone is no longer a sign-in, and the first step says so rather than failing.
        var password = await authenticator.AuthenticateAsync("admin", Password);
        Assert.True(password.IsSuccess);
        Assert.True(await twoFactor.IsEnabledAsync(userId));

        // The next step's code: the current one confirmed the enrollment a moment ago, and is spent.
        var challenge = await twoFactor.IssueChallengeAsync(userId);
        var redeemed = await twoFactor.RedeemChallengeAsync(challenge, NextCode(secret));

        Assert.True(redeemed.IsSuccess, redeemed.IsSuccess ? null : redeemed.Error.Message);
        Assert.Equal(userId, redeemed.Value);

        // Spent: a challenge answers once, so an intercepted one is worth nothing afterwards.
        var replayed = await twoFactor.RedeemChallengeAsync(challenge, NextCode(secret));
        Assert.True(replayed.IsFailure);
    }

    [Fact]
    public async Task A_code_that_signed_in_cannot_sign_in_again_while_it_is_still_valid()
    {
        // A code stays valid for up to ninety seconds. One read over a shoulder, or typed into a
        // phishing page that relays it, used to open a second session with the password alone.
        var userId = await CreateAdminAsync();
        var secret = await EnrollAsync(userId);

        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var code = NextCode(secret);
        var first = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), code);
        Assert.True(first.IsSuccess, first.IsSuccess ? null : first.Error.Message);

        var again = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), code);

        Assert.True(again.IsFailure);
    }

    [Fact]
    public async Task The_code_that_confirmed_the_enrollment_does_not_also_sign_in()
    {
        var userId = await CreateAdminAsync();
        string confirmingCode;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);
            confirmingCode = CurrentCode(enrollment.Value.Secret);
            Assert.True((await twoFactor.ConfirmEnrollmentAsync(userId, confirmingCode)).IsSuccess);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var redeemed = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), confirmingCode);

            Assert.True(redeemed.IsFailure);
        }
    }

    [Fact]
    public async Task A_challenge_survives_only_so_many_wrong_codes()
    {
        var userId = await CreateAdminAsync();
        var secret = await EnrollAsync(userId);

        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var challenge = await twoFactor.IssueChallengeAsync(userId);

        for (var attempt = 0; attempt < LoginChallenge.MaxAttempts; attempt++)
        {
            Assert.True((await twoFactor.RedeemChallengeAsync(challenge, "000000")).IsFailure);
        }

        // Six digits is a million values, which is not many when an attacker who already has the
        // password may keep trying. After the cap the challenge is closed, so the right code no
        // longer opens it either and another password verification is the only way on.
        var correct = await twoFactor.RedeemChallengeAsync(challenge, NextCode(secret));
        Assert.True(correct.IsFailure);
    }

    [Fact]
    public async Task Parallel_wrong_codes_cannot_outrun_a_challenges_attempt_cap()
    {
        var userId = await CreateAdminAsync();
        var secret = await EnrollAsync(userId);

        string challenge;
        await using (var scope = _provider.CreateAsyncScope())
        {
            challenge = await scope.ServiceProvider.GetRequiredService<ITwoFactorService>().IssueChallengeAsync(userId);
        }

        // Each guess in its own scope, as each would be in its own HTTP request. A cap counted in memory
        // after the check lets parallel guesses that read the same count all through while the row
        // records one — which is exactly how someone holding the password would search the code space.
        var guesses = Enumerable.Range(0, LoginChallenge.MaxAttempts * 3).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ITwoFactorService>()
                .RedeemChallengeAsync(challenge, "000000");
        });
        Assert.All(await Task.WhenAll(guesses), r => Assert.True(r.IsFailure));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var correct = await twoFactor.RedeemChallengeAsync(challenge, NextCode(secret));
            Assert.True(correct.IsFailure);

            var row = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
                .LoginChallenges.SingleAsync(c => c.UserId == userId.Value);
            Assert.Equal(LoginChallenge.MaxAttempts, row.Attempts);
            Assert.NotNull(row.ConsumedAt);
        }
    }

    [Fact]
    public async Task A_recovery_code_presented_twice_at_once_signs_in_only_once()
    {
        var userId = await CreateAdminAsync();
        var codes = await EnrollAndCollectRecoveryCodesAsync(userId);

        var challenges = new List<string>();
        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            for (var i = 0; i < 6; i++)
            {
                challenges.Add(await twoFactor.IssueChallengeAsync(userId));
            }
        }

        // Single use has to hold when the uses race: two sign-ins that both read the code as unspent
        // would otherwise both be let in by one piece of paper.
        var redeemed = await Task.WhenAll(challenges.Select(async challenge =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ITwoFactorService>()
                .RedeemChallengeAsync(challenge, codes[0]);
        }));

        Assert.Equal(1, redeemed.Count(r => r.IsSuccess));
    }

    [Fact]
    public async Task Confirming_again_once_the_factor_is_on_is_refused_and_keeps_the_recovery_codes()
    {
        var userId = await CreateAdminAsync();

        string secret;
        IReadOnlyList<string> codes;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);
            secret = enrollment.Value.Secret;
            codes = (await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(secret))).Value;
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

            // A session is all confirming asks for, so on an account that already has the factor it
            // must not be a way to mint a fresh batch: whoever guessed one valid code would walk away
            // with ten credentials that outlive the session and void the owner's own.
            var again = await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(secret));

            Assert.True(again.IsFailure);
            Assert.Equal("identity.two_factor_already_enabled", again.Error.Code);

            var redeemed = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), codes[0]);
            Assert.True(redeemed.IsSuccess, redeemed.IsSuccess ? null : redeemed.Error.Message);
        }
    }

    [Fact]
    public async Task A_pending_enrollment_survives_only_so_many_wrong_codes()
    {
        var userId = await CreateAdminAsync();

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);

            for (var attempt = 1; attempt < User.MaxEnrollmentConfirmAttempts; attempt++)
            {
                var wrong = await twoFactor.ConfirmEnrollmentAsync(userId, "000000");
                Assert.Equal("identity.invalid_two_factor_code", wrong.Error.Code);
            }

            // The last one says the setup is gone, so the client can ask for the password rather than
            // another code that could never work.
            var last = await twoFactor.ConfirmEnrollmentAsync(userId, "000000");
            Assert.Equal("identity.two_factor_enrollment_abandoned", last.Error.Code);

            // The same shape as a sign-in challenge: past the cap the enrollment is gone, so the right
            // code no longer turns anything on and the only way forward is the password again.
            var correct = await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(enrollment.Value.Secret));
            Assert.True(correct.IsFailure);
            Assert.Equal("identity.two_factor_not_enrolled", correct.Error.Code);
            Assert.False(await twoFactor.IsEnabledAsync(userId));
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
                .Users.SingleAsync(u => u.Id == userId.Value);
            Assert.Null(user.TotpSecretCipher);
            Assert.Equal(0, user.TotpConfirmAttempts);
        }

        // Starting over with the password gives a fresh budget, so somebody who fumbled their codes is
        // inconvenienced rather than locked out.
        await EnrollAsync(userId);
    }

    [Fact]
    public async Task Parallel_wrong_codes_cannot_share_an_attempt()
    {
        var userId = await CreateAdminAsync();
        string secret;
        await using (var scope = _provider.CreateAsyncScope())
        {
            secret = (await scope.ServiceProvider.GetRequiredService<ITwoFactorService>()
                .BeginEnrollmentAsync(userId, Password)).Value.Secret;
        }

        // Each request in its own scope, as each would be in its own HTTP request. Counted in memory
        // after the check, parallel requests that all read the same count would each get a guess while
        // the row recorded one; spent atomically first, the cap holds however they interleave.
        var attempts = Enumerable.Range(0, User.MaxEnrollmentConfirmAttempts * 3).Select(async _ =>
        {
            await using var scope = _provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ITwoFactorService>()
                .ConfirmEnrollmentAsync(userId, "000000");
        });
        var results = await Task.WhenAll(attempts);

        // Exactly the cap's worth of attempts is taken: all but one answer "wrong code", the one that
        // drops the enrollment says so, and every other request is refused before its code is looked at.
        Assert.All(results, r => Assert.True(r.IsFailure));
        Assert.Equal(
            User.MaxEnrollmentConfirmAttempts - 1,
            results.Count(r => r.Error.Code == "identity.invalid_two_factor_code"));

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
            Assert.True((await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(secret))).IsFailure);
            Assert.False(await twoFactor.IsEnabledAsync(userId));
        }
    }

    [Fact]
    public async Task Beginning_an_enrollment_again_resets_the_wrong_code_budget()
    {
        var userId = await CreateAdminAsync();

        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

        await twoFactor.BeginEnrollmentAsync(userId, Password);
        for (var attempt = 0; attempt < User.MaxEnrollmentConfirmAttempts - 1; attempt++)
        {
            await twoFactor.ConfirmEnrollmentAsync(userId, "000000");
        }

        var restarted = await twoFactor.BeginEnrollmentAsync(userId, Password);
        await twoFactor.ConfirmEnrollmentAsync(userId, "000000");

        var confirmed = await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(restarted.Value.Secret));
        Assert.True(confirmed.IsSuccess, confirmed.IsSuccess ? null : confirmed.Error.Message);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once_and_never_again()
    {
        var userId = await CreateAdminAsync();
        var codes = await EnrollAndCollectRecoveryCodesAsync(userId);

        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

        var first = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), codes[0]);
        Assert.True(first.IsSuccess, first.IsSuccess ? null : first.Error.Message);

        // Single use is the whole contract: a code written on paper and used once must not still be
        // on that paper as a working credential.
        var reused = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), codes[0]);
        Assert.True(reused.IsFailure);

        // A different one from the same batch still works.
        var second = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), codes[1]);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task A_recovery_code_works_when_the_master_key_is_gone()
    {
        var userId = await CreateAdminAsync();
        var codes = await EnrollAndCollectRecoveryCodesAsync(userId);

        // The TOTP secret is encrypted with the installation master key, so losing that key makes
        // every generated code unverifiable. A recovery code is only hashed, which is exactly why it
        // is the way back from a failure that would otherwise be permanent.
        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", null);
        await using var withoutKey = await IdentityTestHost.CreateAsync(
            "cinomni_test_identity_two_factor", configure: null, migrate: false);

        await using var scope = withoutKey.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var redeemed = await twoFactor.RedeemChallengeAsync(await twoFactor.IssueChallengeAsync(userId), codes[2]);

        Assert.True(redeemed.IsSuccess, redeemed.IsSuccess ? null : redeemed.Error.Message);

        Environment.SetEnvironmentVariable("CINOMNI_SECRET_KEY", MasterKeyBase64);
    }

    [Fact]
    public async Task Turning_it_off_needs_both_factors_and_ends_every_other_session()
    {
        var userId = await CreateAdminAsync();
        var secret = await EnrollAsync(userId);

        string keptToken;
        string otherToken;
        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            keptToken = (await sessions.IssueAsync(userId)).Token;
            otherToken = (await sessions.IssueAsync(userId)).Token;
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

            // A session alone cannot undo the protection over the account it belongs to, or a stolen
            // one would simply switch it off.
            var wrongCode = await twoFactor.DisableAsync(userId, Password, "000000", keptToken);
            var wrongPassword = await twoFactor.DisableAsync(userId, "wrong", CurrentCode(secret), keptToken);
            Assert.True(wrongCode.IsFailure);
            Assert.True(wrongPassword.IsFailure);

            // And the two are indistinguishable: otherwise "the code is wrong" confirms a guessed password.
            Assert.Equal(wrongPassword.Error, wrongCode.Error);

            var disabled = await twoFactor.DisableAsync(userId, Password, NextCode(secret), keptToken);
            Assert.True(disabled.IsSuccess, disabled.IsSuccess ? null : disabled.Error.Message);
        }

        await using (var scope = _provider.CreateAsyncScope())
        {
            var sessions = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();

            Assert.False(await twoFactor.IsEnabledAsync(userId));
            // The one that made the change keeps its footing; anything left open elsewhere does not,
            // because the account is less protected now than when that session was opened.
            Assert.True((await sessions.ValidateAsync(keptToken)).IsSuccess);
            Assert.True((await sessions.ValidateAsync(otherToken)).IsFailure);

            // And the codes go with it: they authenticate a factor that no longer exists.
            var remaining = await scope.ServiceProvider.GetRequiredService<IdentityDbContext>()
                .RecoveryCodes.CountAsync(c => c.UserId == userId.Value);
            Assert.Equal(0, remaining);
        }
    }

    // -- helpers ---------------------------------------------------------------------------------

    private async Task<UserId> CreateAdminAsync()
    {
        await using var scope = _provider.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<IUserProvisioning>();
        var created = await provisioning.CreateAdminAsync("admin", Password);
        Assert.True(created.IsSuccess, created.IsSuccess ? null : created.Error.Message);
        return created.Value;
    }

    private async Task<string> EnrollAsync(UserId userId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);
        Assert.True(enrollment.IsSuccess, enrollment.IsSuccess ? null : enrollment.Error.Message);

        var confirmed = await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(enrollment.Value.Secret));
        Assert.True(confirmed.IsSuccess, confirmed.IsSuccess ? null : confirmed.Error.Message);
        return enrollment.Value.Secret;
    }

    private async Task<IReadOnlyList<string>> EnrollAndCollectRecoveryCodesAsync(UserId userId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var twoFactor = scope.ServiceProvider.GetRequiredService<ITwoFactorService>();
        var enrollment = await twoFactor.BeginEnrollmentAsync(userId, Password);
        Assert.True(enrollment.IsSuccess, enrollment.IsSuccess ? null : enrollment.Error.Message);

        var confirmed = await twoFactor.ConfirmEnrollmentAsync(userId, CurrentCode(enrollment.Value.Secret));
        Assert.True(confirmed.IsSuccess, confirmed.IsSuccess ? null : confirmed.Error.Message);
        Assert.Equal(10, confirmed.Value.Count);
        return confirmed.Value;
    }

    /// <summary>
    /// The code for the step after this one, still inside the accepted window. What a test needs once the
    /// current step has been spent — by the enrollment's confirmation, typically.
    /// </summary>
    private static string NextCode(string base32Secret)
    {
        Assert.True(Base32.TryDecode(base32Secret, out var secret));
        return Totp.Generate(secret, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)Totp.Step.TotalSeconds) + 1);
    }

    /// <summary>The code an authenticator would be showing right now for this secret.</summary>
    private static string CurrentCode(string base32Secret)
    {
        Assert.True(Base32.TryDecode(base32Secret, out var secret));
        return Totp.Generate(secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (long)Totp.Step.TotalSeconds);
    }
}
