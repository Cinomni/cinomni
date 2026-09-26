# Notifications

Cinomni tells people about things that happened, in two places.

## In the app

The bell at the top of the page lists notifications for your account. They arrive live, without
reloading.

| Notification | Who sees it |
|---|---|
| A title is ready to watch | Accounts that can see the title |
| Cinomni could not get a title | Accounts that can see the title |
| Someone requested a title | Administrators |
| A metadata provider is failing | Administrators |
| The VPN tunnel stopped carrying torrent traffic, or started again | Administrators |

## Channels

A channel sends the same notifications outside the app. Add one under **Console → Channels → Add
channel**:

| Kind | Target |
|---|---|
| **Discord** | A Discord webhook URL (`https://discord.com/api/webhooks/…`), created in the channel's settings on Discord. |
| **Webhook** | Any HTTPS endpoint that accepts a JSON `POST`. |

Treat the target URL as a password: whoever has a webhook URL can post to it. It is stored in the
database and included in database backups, and it is shown on the Channels page. Deleting a channel is
immediate; to restore it you need the URL again.

Delivery is best effort. A channel that is down never blocks the rest of Cinomni, and the in-app
notification is always recorded.

Like indexers, a channel target cannot be an address on your own machine or local network.
