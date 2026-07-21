# Discord core plugin

The Discord core plugin uses the official Discord Social SDK. Its normal user flow is:

1. Select **Connect Discord** in Tools for Steam.
2. Sign in in the browser window opened by Discord.
3. Review and approve friends, presence, and server-list access.
4. Return to Tools for Steam to browse currently online friends and Discord servers.
5. Pin frequently used servers from **Manage Server Favorites**, or use the controller Menu/Options button on a server row. The Discord overview keeps favorites in its first collapsible section, followed by collapsible friends, other servers, and management sections.
6. Optionally enable **Notify When a Friend Comes Online** in Discord Settings.

Discord Desktop is optional for sign-in. The Social SDK can use the browser flow directly.

## Discord application configuration

The production application is `1526906410359848990`. In the
[Discord Developer Portal](https://discord.com/developers/applications), the publisher must:

- accept the Social SDK terms;
- enable **Public Client** so the desktop app contains no client secret;
- request the presence scopes plus standard server-list access: `openid sdk.social_layer_presence guilds`;
- complete Discord's distribution/approval requirements before publishing to all Store users.

The public application ID is embedded in the build. A separate developer application can be used:

```powershell
dotnet publish src/SteamLoader.App/SteamLoader.App.csproj -c Release -p:DiscordClientId=123456789012345678
```

`TOOLS_FOR_STEAM_DISCORD_CLIENT_ID` is also supported for local builds. Store users do not need to
enter an application ID.

## Runtime packaging

`ThirdParty/DiscordSocialSdk` contains the Discord Social SDK 1.9.17379 download for Windows x64
(the bundled DLL reports runtime version 1.9.17380). The project
automatically copies `discord_partner_sdk.dll`, `License-Notices.txt`, and `NOTICE.txt` into build and
publish output, so the installer and Store package receive the runtime with the app.

## Security

- The Discord password, client secret, and bot token are never requested or stored.
- Discord owns the browser sign-in and permission screen.
- OAuth access and refresh tokens are encrypted with Windows DPAPI for the current Windows user.
- Tokens are never exposed through the local Quick Access API response.
- **Disconnect Discord Account** removes local authorization tokens.
- Favorite server IDs and the friend-online notification preference are stored in the local Discord settings.
- Friend-online notifications are disabled by default. When enabled, presence is checked in the background and only later offline-to-online transitions create an in-Steam TFS notification; the initial online list is treated as a silent baseline.
- The plugin requests friends, presence, and server-list access, not messaging or communication access.

## Server presence limitations

The standard `guilds` OAuth scope allows Tools for Steam to list the signed-in user's servers. The
supported `with_counts=true` response includes approximate member and presence counts. Discord does
not expose the identities of individual online members to user OAuth integrations. The legacy local
RPC `GET_GUILD` member list is deprecated and always empty, so the plugin shows the supported count
and can open the selected server directly in Discord.

## Optional public widget fallback

The previous public-server widget remains optional. It requires a server ID and the server owner's
**Server Settings > Engagement > Server Widget** option. It is not required for the Social SDK friends
list and is kept only as a public server-presence fallback.
