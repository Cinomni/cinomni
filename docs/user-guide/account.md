# Your account

**Your account** (in the side bar) shows who you are signed in as, your role, and what you are allowed
to watch and request.

## Two-factor sign-in

Two-factor sign-in asks for a six-digit code from an authenticator app (any app that supports
time-based codes) after your password. It is optional and per account. Turn it on if your Cinomni is
reachable from the internet, and always for an administrator.

To turn it on:

1. **Your account → Two-factor authentication**, then confirm your password.
2. Scan the code with your authenticator app, or type the **setup key** into it.
3. Enter the **code from your app** to confirm.
4. Cinomni shows **ten recovery codes**, once. Save them somewhere safe, away from your phone, and
   confirm that you have.

From then on, sign-in asks for the authentication code after the password.

### Recovery codes

If you lose your phone, sign in with a recovery code instead of the authentication code. Each code
works once. When you are back in, turn two-factor off and on again to get a new authenticator and a
new set of codes.

If an administrator loses both the authenticator and every recovery code, the way back needs shell
access to the server; see
[DEPLOYMENT.md, "If you are locked out of an account"](../../DEPLOYMENT.md#if-you-are-locked-out-of-an-account).

### Turning it off

**Two-factor authentication → Turn off** asks for your password and a current code. Afterwards the
password alone signs the account in, and every other session of the account is signed out.

## Passwords

An account cannot change its own password from the interface yet; it is on the
[roadmap](../../ROADMAP.md). Your password is set by whoever created your account.

## Signing out

**Sign out** at the bottom of the side bar ends the session on this browser.
