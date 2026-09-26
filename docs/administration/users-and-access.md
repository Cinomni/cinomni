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

To keep some titles for certain people, create a restricted collection, grant it to them, and move the
titles into it. Administrators always see everything.

> The web interface does not move a title between collections yet. Until it does, an administrator
> can do it through the API: `PUT /api/catalog/works/{workId}/collection` with
> `{"collectionId": "<id>"}`.

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
