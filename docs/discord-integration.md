# Discord core plugin

The Discord core plugin uses the official Discord Social SDK. Its normal user flow is:

1. Select **Connect Discord** in Tools for Steam.
2. Sign in in the browser window opened by Discord.
3. Review and approve friends, presence, and server-list access.
4. Return to Tools for Steam to browse currently online friends and Discord servers.
5. Pin frequently used servers from **Manage Server Favorites**, or use the controller Menu/Options button on a server row. The Discord overview keeps favorites in its first collapsible section, followed by collapsible friends, other servers, and management sections.
6. Optionally enable **Notify When a Friend Comes Online** in Discord Settings.

Discord Desktop is optional for sign-in. The Social SDK can use the browser flow directly.

## Discord Web in Big Picture

**Open Discord** on the Discord page (Quick Access) opens discord.com as a Big Picture page
(`/tfs/discord`, `Assets/tfs-discord.mjs`). It works without connecting the Social SDK account;
sign in on Discord's own page (the QR code from the Discord phone app is easiest).

- The page is a Steam BrowserView created through Big Picture's own window
  (`GamepadUIMainWindowInstance.CreateBrowserView`) and rendered with Steam's own browser host
  component, the same one Steam uses for its built-in web browser. Steam therefore provides the
  web-browser controller layout (right stick = mouse, RT = click, LT = right click, left stick =
  scroll) and its on-screen keyboard for `<input>`/`<textarea>` fields.
- Steam ignores contenteditable editors such as Discord's message box. A second page helper
  (`DiscordWebBridge.KeyboardHelperScript`) reports trusted clicks into any text field (and Tab
  focus) through the DevTools binding `tfsDiscordKeyboard` ("show"/"hide" only, never text).
  The backend forwards that to `ToolsForSteamDiscord.keyboard()` in SharedJSContext, which calls
  the BrowserView's own keyboard reference (`m_refKeyboard.ShowVirtualKeyboard`), the same call
  Steam makes for inputs. Discord focusing the box by itself (channel switch) opens nothing.
- Steam Controller 2 / Steam Deck extras: **L4/R4** page up/down in the chat, **L5** mark read,
  **R5** jump to newest messages, **right stick click** right-clicks at the cursor. The Steam and
  Quick Access buttons are never consumed by the page.
- TFS adds: **A** click at the cursor, **B** closes Discord's popups first and leaves the page
  when none is open, **X** mute, **Y** quick switcher, **LB/RB** previous/next server, **D-pad
  up/down** previous/next channel, **D-pad left/right** previous/next unread channel, **View**
  deafen, **Menu** opens the Discord menu (mute, deafen, direct messages, mark read, reload,
  leave, close). Steam offers no input API for BrowserViews, so these go through the backend
  (`/api/discord/web/*`, `DiscordWebBridge`) as DevTools input into the Discord page. Only a fixed
  list of Discord shortcuts is accepted.
- Leaving the page keeps Discord running, so voice continues in the background. **Close
  Discord** (Discord menu or Quick Access) ends it. While it runs, Quick Access shows
  **Return to Discord**, mute and deafen.
- Selecting a server or an online friend on the Discord page opens it in Discord Web (in-app
  navigation, no reload). The server details page still offers the desktop app.
- The page helper injected into discord.com reads only the cursor position, the number of open
  popups, the user panel's mute/deafen switches and whether a voice connection is shown. It never
  reads messages, tokens or form values. A drawn cursor follows the mouse for handhelds where
  Windows hides the pointer.
- Microphone: Steam's BrowserView has no permission prompt, so the bridge sets the DevTools
  permission override for Discord's origins (logged in `data\discord-web.log` when CEF refuses).

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
