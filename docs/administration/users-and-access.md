# Users and access

## Roles

| Role | Can |
|---|---|
| **Administrator** | Operate the installation: everything in **Administration** and **Activity**, add and remove titles, approve requests. |
| **Member** | Browse, watch, and request titles if allowed. |

The first account, created at setup, is an administrator. There can be several administrators.

## Creating accounts

Under **Console → Users → Add user**, enter a username, a password of at least 8 characters and a
role. Give the password to the person; they can sign in straight away.

## Per-account permissions

Each member account has:

| Setting | Meaning |
|---|---|
| **Can request titles** | Off makes the account a viewer only. |
| **Requests skip approval** | Their requests are approved as soon as they are made. |
| **Open requests** | How many requests the account may have open at once: *Installation default*, *No limit*, or *At most* a number. The installation default is **Open requests per account** under **Console → Settings → Requests** (0 means no limit). |
| **Content ceiling** | The highest age rating the account may see. See below. |

An account can also be **disabled**, which stops it signing in without deleting it, and **enabled**
again later. The role of an existing account can be changed.

## Collections

Collections (**Console → Collections**) are the shelves titles sit on, and they decide who sees what.
Every title belongs to exactly one collection. A fresh installation has a single collection,
**Library**, open to everyone, and every title starts there.

Each collection holds movies, series, or both, and is either:

- **Open to everyone**: every account sees its titles; or
- **Only who I grant**: only the member accounts you tick see its titles.

To keep some titles for certain people, create a restricted collection, grant it to them, and give
it rules that pick those titles. Administrators always see everything.

### Rules

A collection can carry a rule: one or more conditions that must all hold. A condition compares one
fact about a title:

| Field | Compared with |
|---|---|
| Kind | *is any of* / *is none of*: movie, series |
| Genre, Content rating, Original language | *is any of* / *is none of*: a list of values |
| Year | *is any of* / *is none of* a list, or *is at least* / *is at most* one year |
| Runtime (minutes) | *is at least* / *is at most* |
| Title | *contains* / *starts with*, plain text, not case sensitive |

A condition never matches a fact the title does not have: a title with no known year matches neither
*year is 1999* nor *year is none of 1999*. Whether a title is downloaded or how many episodes it has
are not fields, on purpose: a title must not change who can see it because a download finished.

A rule only places titles its collection can hold: a movies-only collection never claims a series.

### Evaluation order

A title can only be in one collection, so when the rules of several collections match it, the
**evaluation order** at the top of **Console → Collections** decides: the first collection whose rule
matches claims the title. A title no rule claims goes to the default collection, **Library**, which
is open. That includes a title that stops matching, for example after you change a rule.

### Preview before saving

Saving a rule, removing one, or changing the order can move many titles at once, and moving a title
changes who can see it. So **Save** only becomes available after **Preview**, which shows:

- **Match**: the titles the rule matches;
- **Would move**: the titles that would change collection, each with where it is and where it would go;
- **Pinned, left alone**: titles the rule would move but that stay because they are pinned (below).

When titles would leave a restricted collection for an open one, the preview and the confirmation
both warn that more people would see them.

To remove a collection's rule, remove all its conditions, then preview and save.

### Titles placed by hand (pinned)

Moving a title to a collection by hand **pins** it there: rules no longer move it until the pin is
released. Releasing the pin places the title wherever the rules say at that moment.

> The web interface does not move a single title or release a pin yet. Until it does, an
> administrator can do both through the API: `PUT /api/catalog/works/{workId}/collection` with
> `{"collectionId": "<id>"}` moves and pins, and `DELETE /api/catalog/works/{workId}/collection-pin`
> releases the pin.

When you upgrade to a version with rules, titles already outside **Library** are pinned, so saving
the first rule cannot move them.

### New titles while rules exist

When a title is added, Cinomni only knows its kind, title and year; its genres and age rating arrive
with its metadata a moment later. So while at least one rule exists, a new title is **hidden from
members until its metadata arrives**, and then placed by the rules. This keeps a title a rule would
restrict from being visible on the open **Library** in the meantime.

Administrators see these titles, with a note on the title's page. If the metadata does not arrive,
use **Refresh metadata** from the title's menu, or move the title by hand, which also releases it.
A title added without a provider id is never held, because no metadata would ever arrive for it.

## Content ceiling (parental control)

A content ceiling hides titles rated above a given age classification, for one account.

1. Choose a region under **Console → Settings → Metadata → Classification region**. Supported:
   `US`, `ES`, `DE` and `GB`. Empty (the default) reads no classification at all, and no ceiling
   applies.
2. Under **Console → Users**, pick the ceiling for the account, for example `PG-13` or `12`.

Titles above the ceiling are hidden everywhere for that account: lists, search, Upcoming and direct
links. **Titles with no rating stay visible.** A ceiling set for one region is not applied while the
installation uses another region; the page says so.

The account holder can see the limit under **Your account → What you can watch**.

## Sign-in protection

- Sign-in and setup attempts are rate-limited per client; see **Console → Settings → Security**. Behind
  a reverse proxy this only works if the proxy is declared; see
  [Networking](networking.md#tell-cinomni-about-the-proxy).
- Each account can turn on [two-factor sign-in](../user-guide/account.md).
