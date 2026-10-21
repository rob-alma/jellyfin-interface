# Streamline for Jellyfin

A Jellyfin server plugin that replaces the web client's home screen (and the Movies and Shows
pages) with a streamlined, cinematic layout, in the style of the big streaming services:

- **Hero banner**: full-width, auto-rotating backdrops of what's *new*: movies released in the last
  30 days, and shows that are brand new or got a new season or new episodes in that time (by air
  date), each labelled (New Release, New Series, New Season, New Episodes). Shows you already watch
  are included when they have new episodes. If fewer than 3 titles qualify, it's topped up with what
  was most recently added to the server. Title logo, ratings, year, genres, overview, and **Play**,
  **More info** and **Watch List** buttons. Pauses on hover, when scrolled away and when the tab is in
  the background; swipe on touch screens; no auto-rotation with "reduce motion" on.
- Then, top to bottom:
  1. **Continue Watching**: half-watched movies and episodes *and* the next episode of every show in
     progress, in one row. Clicking a card starts playback right away.
  2. **Recommended For You**, when the [Personal Recommendations](../jellyfin-plugin-personal-recommendations)
     plugin is installed (skipped without it).
  3. **New on Jellyfin**: recently added.
  4. **Coming Soon** (below).
  5. **Watch List**: the watchlist (below), then **Favorites**: Jellyfin's favorites (the heart).
  6. **Genre rows**, per person: the admin's list (Action, Drama, Crime, Fantasy, Adventure, Romance,
     Comedy by default) in the order of what that person watches, favorites and rates (genres they
     rated down sink; others they watch a lot can come in). **Customize** on the first genre row: pin
     genres to the top in your own order (shown even with one title), hide genres, or switch the
     automatic order off. Stored per user; the taste is worked out at night and updated per title
     during the day. Without any history: the admin's order. Rows with fewer than 4 titles are skipped.
  7. Two big **Series** and **Movies** buttons, for when nothing above caught the eye.
- **Every row ends in a "See all" card** (and its title has an "Explore all" link) that opens a
  streamlined grid of everything in it. Every such page can be sorted by top rated, recently added,
  release date or title, with a button to flip the order (newest/oldest first, A–Z/Z–A). On home
  they mix movies and shows; from the Movies or Shows page they only cover that library. **New on
  Jellyfin** shows everything added in the last 30 days, and **Watch List** in the nav bar opens the
  same kind of page.
- **"New" tags** (blue, top left) on every card, in every row and grid, and in the banner, going
  by air date: **New Series** while a show's only season is airing and **New Episodes** for a new
  season or new episodes of an older show, each until 30 days after the latest episode; **New
  Release** for movies until 60 days after release. At most one tag per card; the periods are settings.
- **Movies and Shows pages**: their first tab gets the same treatment: a banner of what's new in that
  library, then Watch List, Recommended For You, Critically Acclaimed, the main genres, **sub-genres**,
  and every other genre (loaded as you scroll), ending in **All Movies/All Shows** and **All Genres**
  tiles. Sub-genres are genre pairs (Sci-Fi Action, Crime Dramas, Romantic Comedies…) and themes from
  TMDb keywords, which Jellyfin keeps as tags (Dystopian, Space, Time Travel, Heists, Based on a
  Book…); a fixed list, since raw keywords are noisy. Rows with fewer than 4 titles are skipped.
  **All Genres** shows poster-shaped tiles with a 2×2 collage of what's inside: the headline genres
  (Action, Drama, Adventure, Fantasy, Sci-Fi, Romance, Comedy, Horror, Thriller; a setting) larger,
  then sub-genres and every other genre.
- **Genres for shows**: TMDb's TV genres have no Romance, Horror or Thriller and lump Fantasy in
  with Sci-Fi ("Sci-Fi & Fantasy"). For shows those genres come from their TMDb keywords instead
  (romance/love, magic/dragon/witch, space/dystopia/time travel…), and the combined TV genres are
  split into their parts. Movies use their real genres.
- **Watch List vs Favorites**: two separate things. **Watch List** (+) is a watchlist: movies and shows
  someone still wants to watch, kept per user by the plugin (Jellyfin has no watchlist of its own,
  and its playlists turn a show into its episodes), stored as a small file per user in the plugin's
  data folder. A title drops off it once that user has watched it (a
  show: its last episode), unless they add it again after watching. Every Watch List call works on
  the signed-in user's own list only (there's no user id to pass), and Jellyfin itself refuses to show
  one user another's favorites. **Favorites** (♥) are Jellyfin's own favorites, the
  same heart as on its item pages. Previews and the banner have both buttons; item pages get a +
  next to Jellyfin's heart. Home, Movies and Shows show a Watch List row and a Favorites row.
- **Ratings**: a thumbs button next to Watch List (in hover previews, the banner and on item pages)
  opens five faces: Terrible, Bad, Okay, Good, Amazing. Each rating is stored in that user's own
  Jellyfin data (the item's user rating, as 2/4/6/8/10 out of 10; the like/dislike flag is kept in
  step for other apps). Ratings are private: nobody sees how anyone else rated, and they only feed
  that user's own Recommended For You row (Personal Recommendations 0.5.0+).
- **Trailers on item pages**: every movie, show, season and episode page gets a trailer section,
  under Next Up and above the seasons (seasons and episodes use their show's trailer), from the
  YouTube links TMDb provides. The server checks each link with YouTube's public oEmbed endpoint
  (no API key): dead or embed-blocked videos are dropped, real titles are shown, and when there
  are real trailers, TV spots and ads are left out. When TMDb has no trailer, one is found automatically (below). Only a thumbnail loads until someone presses play (then it plays
  from youtube-nocookie.com); it stops when you leave the page. Jellyfin's pages send no referrer,
  which YouTube now rejects for embeds ("error 153"), so the trailer frame alone sends the
  server's address. A "Watch on YouTube" link sits underneath. Can be switched off.
- **Item page buttons**: the same round buttons as everywhere else: play, + Watch List, ♥ Favorite,
  rating, ✓✓ watched. Jellyfin's own watched check, heart and shuffle are hidden (so there aren't two check
  marks side by side), and its Trailer button too when the trailer is embedded.
- **New media gets its genres and trailer automatically**: movies and shows arrive through
  Jellyfin's library scan however they were added (Radarr/Sonarr after a Seerr request, or by hand),
  and the scan fetches their metadata from TMDb. Brand-new releases often have no trailer or keywords
  there yet, so 15 minutes after a title is added anything still missing (genres, keywords, trailer,
  a TMDb match) is fetched again, and the daily task *Fill in missing genres and trailers* keeps
  retrying titles added in the last 60 days. It only fills gaps; nothing already there (or locked)
  is replaced. The genre rows and pages are worked out live from Jellyfin's genres and TMDb
  keywords, so new titles show up in them as soon as that metadata is there.
- **Trailers for everything, automatically**: a title TMDb has no trailer for gets one from
  KinoCheck (a free trailer database built on YouTube, looked up by TMDb id, no key needed), or, as
  a last resort, from a YouTube search if a YouTube Data API key is set in the settings. The trailer
  is saved into the title's own Jellyfin metadata, so users can't change it. This runs when a title
  arrives, daily for the whole library, and when an item page is first opened. Titles with no
  trailer anywhere simply have no trailer section.
- **Wrong titles and posters get fixed**: each movie and show is compared with its folder name and
  year (what Radarr/Sonarr or you named it, including Radarr's `{tmdb-123}`). When Jellyfin's match
  doesn't fit (another name and year, or no match at all), it's looked up again and re-identified
  if exactly one result clearly fits, the same as using Identify by hand; missing posters are
  fetched again. Runs on arrival and daily (*Fix wrong titles and posters*). Locked titles are never
  touched. About 10 minutes after an automatic fix it's checked again; anything still wrong, without a
  poster, or uncertain is listed on the settings page (Open / Done) for you to fix by hand.
- **Seerr requests land on the Watch List**: with Seerr's (Jellyseerr/Overseerr) address and API key
  set, whatever someone requested goes on their Watch List as soon as it's in Jellyfin. The plugin
  polls Seerr's request list (every 10 minutes and right after new titles arrive) and matches the
  requester by their linked Jellyfin account (Jellyseerr) or user name. Each request is added once
  (removing it afterwards sticks), including requests made before Seerr was connected.
- **Collections** (top bar and phone tab bar), in this order:
  1. **Featured**: Disney, Marvel (MCU and X-Men), DC (Batman, Superman and the rest), Pixar, Book
     Adaptations, Musicals, Best Picture Winners, Award-winning Shows (Emmy winners), Studio Ghibli,
     DreamWorks Animation, Christmas, Halloween, A24, Stephen King, Video Game Adaptations, Under 90
     Minutes; each from 3 titles. Admins add their own under *Extra collections* (`Name = Title;
     Title`); those come first.
  2. **Franchises** across movies and shows: a built-in list (MCU, X-Men, Star Wars, Batman,
     Superman, Breaking Bad, Game of Thrones, Middle-earth, Wizarding World, The Walking Dead and
     more), 2 titles when a show is in it, otherwise 3 movies. Any TMDb movie series with 2 or more
     movies here also becomes a franchise by itself when a spin-off show is on the server: a show
     named after one of its movies ("Jurassic World: Camp Cretaceous", with or without the colon;
     without it the movie's name must be two words or more).
  3. **Movie series**: every other TMDb movie collection with at least 3 movies here (James Bond,
     Jurassic Park...), found automatically, plus collections made in Jellyfin.
  4. **Decades**: Classics (before 1980), 80s, 90s, 00s, 10s, 20s.
  5. **Directors**, A to Z, with 4 or more titles here: their movies, and shows where they
     directed the first episode or a third of the episodes.
  Collections made in Jellyfin itself are left out unless *Also show collections made in Jellyfin*
  is on. Everything is worked out from the whole library, not only new arrivals: once on the first
  start after installing, then nightly (*Prepare collections*, 5:00), saved to the plugin's data
  folder; during the day new titles are worked in at most every 30 minutes. Each opens a page in
  release order. Only titles the user can see count. Search finds them by name
  or alias ("007", "dc"), by a title in them ("Skyfall" finds James Bond), or by genre ("superhero"
  finds Marvel and DC).
- Home has a **Collections** row, and item pages show what a title is **part of** (franchise, movie
  series, themes, "More from" its director), each a link.
- **Search** lists people first, then collections, genres and titles. It forgives a typo or two
  (in titles when the normal search finds little), and understands Dutch genre and collection words
  ("oorlogsfilms", "romantische komedie", "jaren 80").
- **Menus**: Jellyfin's side menu is only for admins and only has Home, Dashboard and Metadata Manager (the dashboard's own menu gets a Home item too)
  (Movies and Shows are in the top bar). The person icon opens Settings and Sign out for everyone.
- **Search** also matches genres and sub-genres by name or keyword ("musical", "superhero",
  "anime"), showing their tile and titles; "war movies" / "history shows" narrow to one type.
- **Coming Soon**: episodes about to air, the same for everyone: the next episode of every show on the
  server airing within the month, only for a new show or a new season (its season started in the last
  90 days); requests aren't listed. Shows only, so not on the Movies page. A
  card opens the show's page; a brand-new show (S1:E1) doesn't open. + and the heart are the show's.
  Air dates come from Seerr (TMDb), worked out in the background every 3 hours; without Seerr the row
  is skipped.
- **Search**: a big search field, genre shortcuts, results as posters (and matching people), and
  what's popular here before you type.
- **Phones**: a tab bar at the bottom (Home, Movies, Shows, Watch List, Favorites).
- **Item pages, tidier**: the trailer sits right under the summary; the wall of TMDb keywords and
  the audio/subtitle pickers are hidden (tracks can be switched in the player; file details are
  under ⋯ > Media Info).
- **Same header on every page** (desktop): menu, logo, then Home / Movies / Shows / Watch List / Favorites.
  Jellyfin's back and home buttons, page title and second row of tabs (Suggestions, Collections…)
  are hidden. Phones keep Jellyfin's own header. The video player is left alone.
- **Hover previews** (mouse only): resting on a card opens a larger preview with play, Watch List (for an episode: its show),
  watched and more-info buttons, ratings, runtime and genres.
- On pages with a banner the header is transparent over it and turns solid once you scroll.
- Phones get a centered banner and poster cards; everything else uses landscape cards. The nav bar
  is hidden below 900px wide (the header has no room); the menu still works there.


Targets **Jellyfin 10.11.x** (builds against `Jellyfin.Controller`/`Jellyfin.Model` 10.11.11, .NET 9),
same as Personal Recommendations.

## Where it works

Only in clients that run Jellyfin's own web interface: browsers, the PWA, and the official
Android/iOS apps. Android TV, Infuse, Swiftfin, Findroid etc. draw their own interface and are
unaffected. Nothing else in Jellyfin changes; detail pages, libraries, the player and the
dashboard are all stock.

## How it gets onto the home screen

Same mechanism as Personal Recommendations (the helpers are shared): a `<script>` tag injected
into `index.html`, through the **File Transformation** plugin, the **JavaScript Injector**
plugin, or by patching `jellyfin-web/index.html` directly (re-applied on every start). See
**Advanced → Frontend injection method** on the settings page. The script loads its own
stylesheet and builds the home screen in the browser, using Jellyfin's normal API as the
signed-in user, so library access and parental controls apply as usual.

Starting playback from the banner, a Continue Watching card or a preview sends the browser's own
session a "play now" command through the server, the same thing remote control does. Jellyfin
doesn't expose its player to plugins any other way.

The plugin adds a few endpoints under `/Streamline`:

| Endpoint | Auth | Purpose |
| --- | --- | --- |
| `GET /Streamline/Watchlist`, `POST`/`DELETE /Streamline/Watchlist/{itemId}` | signed in | the signed-in user's Watch List |
| `GET /Streamline/Trailers/{itemId}` | signed in | checked, ranked trailers for an item (its show's for seasons/episodes) |
| `GET /Streamline/Admin/Issues`, `POST /Streamline/Admin/Issues/{itemId}/Done`, `POST /Streamline/Admin/SeerrTest` | admin | titles needing a manual fix, mark one handled; test and sync Seerr |
| `GET /Streamline/ComingSoon` | signed in | the Coming Soon row (the same for everyone, cut to what the caller can see) |
| `GET /Streamline/Collections` | signed in | the caller's collections, titles in release order |
| `GET /Streamline/script`, `GET /Streamline/style` | none (loaded before sign-in) | the client script and stylesheet |
| `GET /Streamline/Config` | signed in | the settings the client needs (nothing sensitive) |

Like any plugin that modifies the web client this way, it depends on jellyfin-web's markup
(`#homeTab`, `#moviesTab`, `#seriesTab`, the `#/list` page), which isn't a stable plugin API. A future
jellyfin-web update could break it until the plugin is updated. If the script fails, the stock
pages show as normal; the see-all links still open Jellyfin's own list page.

## Settings

Dashboard → Plugins → Streamline. Everything except the injection method applies on the next page
load, no restart needed.

| Setting | Default |
| --- | --- |
| Use the new home screen | on |
| Also streamline the Movies and Shows pages | on |
| Accent color | `#aa5cc3` (Jellyfin light purple) |
| Hero banner on / feature titles from the last ... days / number / seconds each | on / 30 / 6 / 9 |
| Tags: New Series and New Episodes / New Release (days); New on Jellyfin covers (days) | 30 / 60; 15 |
| Phone preview and User view buttons (for testing on a desktop; User view is admins only, signs in as the chosen test account via Quick Connect) | off / none |
| Genre rows | `Action, Drama, Crime, Fantasy, Adventure, Romance, Comedy` |
| Titles per row | 20 |

Ratings come from Jellyfin's metadata: the community rating (TMDb, or IMDb when the built-in OMDb
provider is enabled for the library) and the critic rating (Rotten Tomatoes, OMDb only). Titles
without ratings count as average.

## Item pages

- Episodes and seasons are rated as their show (the button shows the show's rating).
- **Explore** at the bottom: the collections the title is part of and its first two genres.
- **Trailers**, on movie and show pages (not on seasons or episodes): shows with several seasons
  get one per season, as chips (from the titles of the show's trailers, KinoCheck, Seerr's TMDb
  videos, and a YouTube search with an API key). Admins can set a trailer by hand from the page's menu
  (Trailer...): a YouTube link, no trailer, or back to automatic; kept in the plugin's data
  folder, the title's metadata stays as it is.
- A **back arrow** in the top left on every device returns to the same spot on the previous page
  (also with the browser's or phone's back): how far down, and how far each row was scrolled.

## Genres

Only the approved genres are used anywhere (rows, the Genres page, search, Customize, the genre
line on cards): Action, Adventure, Comedy, Crime, Drama, Fantasy, Sci-Fi, Romance, Thriller,
Horror, Mystery, Animation, Family, War, History, Music, Western and Documentaries, plus the
sub-genres. Other genres a library may have (Kids, Soap, Reality, Biography, TV Movie...) are left
out. Dutch genre names from TMDb (Actie, Komedie, Misdaad...) count as their English genres, and
TV's combined genres (Action & Adventure) as their parts.

## Apps

Works in the browser, the PWA and the official apps built on the web client, including Jellyfin
Desktop / Jellyfin Media Player 1.12, which runs on an older Chromium (Qt 5.15): the few things
that engine lacks are filled in or have a CSS fallback. If anything in Streamline fails, it steps
aside and Jellyfin's own pages show. The User view needs File Transformation or direct injection.

## Performance on a big library

The heavy work runs at night (default times, changeable under Dashboard > Scheduled Tasks):

| Time | Task | What |
| --- | --- | --- |
| 3:00 | Refresh personal recommendations (Personal Recommendations) | scoring every user's taste profile; also on server start |
| 4:00 | Fill in missing genres and trailers | metadata gaps and trailers for recent titles |
| 4:30 | Fix wrong titles and posters | at most 40 lookups a night |
| 5:00 | Prepare collections and genre rows | every collection, every movie's directors and writers, and everyone's genre taste |

During the day: a page asks for a prepared, saved list (milliseconds); new titles join collections
within half an hour; new titles get their metadata/trailer/title checks 15 minutes after arriving
(only those titles); rating something refreshes only that user's recommendations. Search's typo
list is the titles' names only, loaded once and kept for a while.

## Building

```bash
dotnet publish Jellyfin.Plugin.Streamline/Jellyfin.Plugin.Streamline.csproj -c Release -o artifacts/publish
```

## Installing

Same as Personal Recommendations: either push a tag like `v0.13.0` and add
`https://github.com/rob-alma/jellyfin-interface/releases/latest/download/manifest.json` as a plugin repository
(the Release workflow builds the zip and manifest), or copy `Jellyfin.Plugin.Streamline.dll` and
`meta.json` from the publish output into a folder like `plugins/Streamline_0.13.0.0/` in Jellyfin's
data directory. Restart Jellyfin either way, then hard-refresh the browser once (or clear the
app's cache) so it picks up the new `index.html`.
