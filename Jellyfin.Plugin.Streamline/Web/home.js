(() => {
    "use strict";

    // Both injection paths (index.html tag, JavaScript Injector loader) can end up on the same
    // page after switching methods; only ever run one copy.
    if (window.__streamlineLoaded) {
        return;
    }
    window.__streamlineLoaded = true;

    // Older engines: Jellyfin Desktop 1.12 (Jellyfin Media Player) runs on Qt 5.15, which is
    // Chromium 83. Fill in what it lacks that's used here.
    [window.Element, window.Document, window.DocumentFragment].forEach((type) => {
        if (type && !type.prototype.replaceChildren) {
            type.prototype.replaceChildren = function replaceChildren(...nodes) {
                while (this.lastChild) {
                    this.removeChild(this.lastChild);
                }
                this.append(...nodes);
            };
        }
    });

    const SCRIPT_URL = document.currentScript ? document.currentScript.src : "";
    const RATE_HINT_KEY = "streamline-rate-hint-seen";
    const ENABLED_KEY = "streamline-enabled";
    const HOME_TAB_SELECTOR = "#indexPage #homeTab.is-active";
    // Jellyfin's light purple (the logo's other colour).
    const DEFAULT_ACCENT = "#aa5cc3";
    const ITEM_FIELDS = "Overview,Genres,ChildCount,DateCreated,SortName";
    const IMAGE_TYPES = "Primary,Backdrop,Logo,Thumb";
    const STABLE_ROW_TTL_MS = 10 * 60 * 1000;
    const PREVIEW_DELAY_MS = 450;
    const RECOMMENDATIONS_RETRY_MS = 4000;
    const COMPACT_QUERY = window.matchMedia("(max-width: 640px)");
    const HOVER_QUERY = window.matchMedia("(hover: hover) and (pointer: fine)");
    const REDUCED_MOTION_QUERY = window.matchMedia("(prefers-reduced-motion: reduce)");

    const state = {
        config: null,
        configUserId: null,
        configPromise: null,
        root: null,
        view: null,
        cache: new Map(),
        cacheUserId: null,
        hero: null,
        preview: null
    };

    // ---------------------------------------------------------------------------------------
    // Small helpers
    // ---------------------------------------------------------------------------------------

    function el(tag, className, text) {
        const node = document.createElement(tag);
        if (className) {
            node.className = className;
        }
        if (text !== undefined && text !== null) {
            node.textContent = text;
        }
        return node;
    }

    function icon(name) {
        const span = el("span", `material-icons ${name}`);
        span.setAttribute("aria-hidden", "true");
        return span;
    }

    function storageGet(key) {
        try {
            return window.localStorage.getItem(key);
        } catch (e) {
            return null;
        }
    }

    function storageSet(key, value) {
        try {
            if (value === null) {
                window.localStorage.removeItem(key);
            } else {
                window.localStorage.setItem(key, value);
            }
        } catch (e) {
            // Private mode / blocked storage: the setting just won't be remembered.
        }
    }

    function userId() {
        try {
            return window.ApiClient ? ApiClient.getCurrentUserId() : null;
        } catch (e) {
            return null;
        }
    }

    function url(path, params) {
        const clean = {};
        Object.keys(params || {}).forEach((key) => {
            if (params[key] !== undefined && params[key] !== null && params[key] !== "") {
                clean[key] = params[key];
            }
        });
        return ApiClient.getUrl(path, clean);
    }

    function getJson(path, params) {
        return ApiClient.getJSON(url(path, params));
    }

    function post(path, params) {
        return ApiClient.ajax({ type: "POST", url: url(path, params) });
    }

    function formatRuntime(ticks) {
        if (!ticks) {
            return "";
        }
        const minutes = Math.round(ticks / 600000000);
        if (minutes < 1) {
            return "";
        }
        if (minutes < 60) {
            return `${minutes}m`;
        }
        const hours = Math.floor(minutes / 60);
        const rest = minutes % 60;
        return rest ? `${hours}h ${rest}m` : `${hours}h`;
    }

    function episodeLabel(item) {
        if (item.Type !== "Episode") {
            return "";
        }
        const season = item.ParentIndexNumber;
        const episode = item.IndexNumber;
        if (season === undefined || episode === undefined) {
            return item.Name || "";
        }
        return `S${season}:E${episode} ${item.Name || ""}`.trim();
    }

    function metaParts(item) {
        const parts = [];
        if (item.Type === "Series") {
            if (item.ProductionYear) {
                parts.push(String(item.ProductionYear));
            }
            if (item.ChildCount) {
                parts.push(item.ChildCount === 1 ? "1 Season" : `${item.ChildCount} Seasons`);
            }
        } else if (item.Type === "Episode") {
            parts.push(`S${item.ParentIndexNumber ?? "?"}:E${item.IndexNumber ?? "?"}`);
            const runtime = formatRuntime(item.RunTimeTicks);
            if (runtime) {
                parts.push(runtime);
            }
        } else {
            if (item.ProductionYear) {
                parts.push(String(item.ProductionYear));
            }
            const runtime = formatRuntime(item.RunTimeTicks);
            if (runtime) {
                parts.push(runtime);
            }
        }
        return parts;
    }

    function progressPercent(item) {
        const data = item.UserData || {};
        if (data.PlayedPercentage) {
            return Math.min(100, data.PlayedPercentage);
        }
        if (data.PlaybackPositionTicks && item.RunTimeTicks) {
            return Math.min(100, (data.PlaybackPositionTicks / item.RunTimeTicks) * 100);
        }
        return 0;
    }

    // ---------------------------------------------------------------------------------------
    // Images
    // ---------------------------------------------------------------------------------------

    function imageUrl(id, type, tag, options) {
        try {
            return ApiClient.getImageUrl(id, Object.assign({ type, tag, quality: 90 }, options));
        } catch (e) {
            return "";
        }
    }

    function backdropUrl(item, maxWidth) {
        if (item.BackdropImageTags && item.BackdropImageTags.length) {
            return imageUrl(item.Id, "Backdrop", item.BackdropImageTags[0], { maxWidth });
        }
        if (item.ParentBackdropImageTags && item.ParentBackdropImageTags.length) {
            return imageUrl(item.ParentBackdropItemId, "Backdrop", item.ParentBackdropImageTags[0], { maxWidth });
        }
        return "";
    }

    function logoUrl(item, maxWidth) {
        if (item.ImageTags && item.ImageTags.Logo) {
            return imageUrl(item.Id, "Logo", item.ImageTags.Logo, { maxWidth });
        }
        if (item.ParentLogoImageTag && item.ParentLogoItemId) {
            return imageUrl(item.ParentLogoItemId, "Logo", item.ParentLogoImageTag, { maxWidth });
        }
        return "";
    }

    function landscapeUrl(item, maxWidth) {
        // Episodes show their series' artwork (as the title treatment does), falling back to
        // the episode's own still; everything else prefers a clean backdrop, then a thumb.
        if (item.Type === "Episode") {
            return backdropUrl(item, maxWidth)
                || (item.ImageTags && item.ImageTags.Primary ? imageUrl(item.Id, "Primary", item.ImageTags.Primary, { maxWidth }) : "");
        }
        return backdropUrl(item, maxWidth)
            || (item.ImageTags && item.ImageTags.Thumb ? imageUrl(item.Id, "Thumb", item.ImageTags.Thumb, { maxWidth }) : "")
            || (item.ImageTags && item.ImageTags.Primary ? imageUrl(item.Id, "Primary", item.ImageTags.Primary, { maxWidth }) : "");
    }

    function posterUrl(item, maxHeight) {
        if (item.Type === "Episode" && item.SeriesPrimaryImageTag) {
            return imageUrl(item.SeriesId, "Primary", item.SeriesPrimaryImageTag, { maxHeight });
        }
        if (item.ImageTags && item.ImageTags.Primary) {
            return imageUrl(item.Id, "Primary", item.ImageTags.Primary, { maxHeight });
        }
        return "";
    }

    function lazyImage(src, className) {
        const img = el("img", className);
        img.alt = "";
        img.loading = "lazy";
        img.decoding = "async";
        img.addEventListener("load", () => img.classList.add("sl-loaded"), { once: true });
        img.addEventListener("error", () => img.remove(), { once: true });
        img.src = src;
        return img;
    }

    // ---------------------------------------------------------------------------------------
    // Actions: navigation, playback, Watch List, watched
    // ---------------------------------------------------------------------------------------

    function showDetails(item) {
        closePreview(true);
        try {
            Emby.Page.showItem(item);
        } catch (e) {
            window.location.hash = `#/details?id=${item.Id}`;
        }
    }

    function navigate(path) {
        try {
            Emby.Page.show(path);
        } catch (e) {
            window.location.hash = `#${path}`;
        }
    }

    async function resolvePlayable(item) {
        if (item.Type !== "Series") {
            return item;
        }

        const uid = userId();
        try {
            const nextUp = await getJson("Shows/NextUp", { UserId: uid, SeriesId: item.Id, Limit: 1, Fields: "UserData", EnableResumable: true });
            if (nextUp && nextUp.Items && nextUp.Items.length) {
                return nextUp.Items[0];
            }
        } catch (e) {
            // Fall through to the first episode.
        }

        const episodes = await getJson(`Shows/${item.Id}/Episodes`, { UserId: uid, Limit: 1, Fields: "UserData" });
        return episodes && episodes.Items && episodes.Items.length ? episodes.Items[0] : null;
    }

    async function play(item) {
        closePreview(true);
        try {
            const target = await resolvePlayable(item);
            if (!target) {
                showDetails(item);
                return;
            }

            // There's no public handle on the web client's own playback manager, so ask the
            // server to tell this very session to play - the same "remote control" message any
            // other client would send, which the web client and the mobile apps both handle.
            const sessions = await getJson("Sessions", { DeviceId: ApiClient.deviceId() });
            const session = (sessions || []).find((s) => s.UserId === userId()) || (sessions || [])[0];
            if (!session) {
                throw new Error("No session for this device");
            }

            const position = target.UserData && target.UserData.PlaybackPositionTicks;
            await post(`Sessions/${session.Id}/Playing`, {
                PlayCommand: "PlayNow",
                ItemIds: target.Id,
                StartPositionTicks: position && !target.UserData.Played ? position : undefined
            });
        } catch (e) {
            console.warn("[Streamline] Could not start playback, opening details instead.", e);
            showDetails(item);
        }
    }

    // Favorites are Jellyfin's own (the heart, the same one as on its item pages).
    async function toggleFavorite(item, button) {
        const next = !(item.UserData && item.UserData.IsFavorite);
        try {
            const data = await ApiClient.updateFavoriteStatus(userId(), item.Id, next);
            item.UserData = Object.assign({}, item.UserData, data || { IsFavorite: next });
            document.querySelectorAll(`[data-fav-for="${item.Id}"]`).forEach((other) => renderFavoriteButton(other, item));
            invalidate("favorites");
        } catch (e) {
            console.warn("[Streamline] Could not update Favorites.", e);
        }
        if (button) {
            button.blur();
        }
    }

    // Watch List is a watchlist: what someone still wants to watch, kept per user by the plugin.
    // The server sends ids with dashes; Jellyfin's items carry the same ids without.
    const normalizeIds = (ids) => (ids || []).map((id) => String(id).replace(/-/g, "").toLowerCase());

    // Collections: franchises across movies and shows, TMDb movie series and Jellyfin's own
    // collections, worked out by the server per user. Titles in release order.
    function loadCollections() {
        return cached("collections", STABLE_ROW_TTL_MS, async () => ((await getJson("Streamline/Collections").catch(() => [])) || [])
            .map((collection) => Object.assign({}, collection, { ItemIds: normalizeIds(collection.ItemIds) })));
    }

    const collectionsHref = () => browseHref(HOME_SCOPE, { sl: "collections" });

    // Collections too broad to be "part of" (decades, All Anime): left out of item pages and the
    // Home row.
    const isBroadCollection = (collection) => collection.Kind === "decade" || collection.Id === "anime-all";
    const collectionHref = (collection) => browseHref(HOME_SCOPE, { sl: "collection", id: collection.Id });

    // A big collection's list only has its first titles; the rest come when it's opened.
    async function collectionIds(collection) {
        if (!collection.Partial) {
            return collection.ItemIds;
        }
        const full = await cached(`collection:${collection.Id}`, STABLE_ROW_TTL_MS, () => getJson(`Streamline/Collections/${encodeURIComponent(collection.Id)}`));
        return normalizeIds((full && full.ItemIds) || []);
    }

    async function collectionMembers(collection) {
        const ids = await collectionIds(collection);
        const items = [];
        for (let i = 0; i < ids.length; i += 50) {
            items.push(...await itemsByIds(ids.slice(i, i + 50)));
        }
        return items;
    }

    // Tiles for the Collections page and search: a few posters each, in release order.
    async function collectionTiles(collections) {
        const firstIds = Array.from(new Set(collections.flatMap((collection) => collection.ItemIds.slice(0, 10))));
        const byId = new Map();
        for (let i = 0; i < firstIds.length; i += 60) {
            (await itemsByIds(firstIds.slice(i, i + 60)).catch(() => [])).forEach((item) => byId.set(normalizeIds([item.Id])[0], item));
        }
        // Posters an earlier tile hasn't shown, where there are enough, so Disney and Pixar
        // (or Marvel and the MCU) don't look the same.
        const used = new Set();
        return collections.map((collection) => {
            const withPoster = collection.ItemIds.map((id) => byId.get(id)).filter((item) => item && item.ImageTags && item.ImageTags.Primary);
            const fresh = withPoster.filter((item) => !used.has(item.Id));
            const items = fresh.concat(withPoster.filter((item) => used.has(item.Id))).slice(0, 4);
            items.forEach((item) => used.add(item.Id));
            return { name: collection.Name, href: collectionHref(collection), kind: collection.Kind, total: collection.Count || collection.ItemIds.length, items };
        });
    }

    function loadWatchlist() {
        return cached("watchlist", STABLE_ROW_TTL_MS, async () => {
            state.watchlist = normalizeIds(await getJson("Streamline/Watchlist"));
            return state.watchlist;
        });
    }

    function inWatchlist(id) {
        return !!state.watchlist && state.watchlist.includes(normalizeIds([id])[0]);
    }

    async function toggleWatchlist(id, button) {
        const adding = !inWatchlist(id);
        try {
            state.watchlist = normalizeIds(await ApiClient.ajax({
                type: adding ? "POST" : "DELETE",
                url: url(`Streamline/Watchlist/${id}`),
                dataType: "json"
            }));
            // The rows read the list through the cache: hand it the updated one.
            state.cache.set("watchlist", { at: Date.now(), promise: Promise.resolve(state.watchlist) });
            document.querySelectorAll(`[data-list-for="${id}"]`).forEach(renderListButton);
            invalidate("mylist");
        } catch (e) {
            console.warn("[Streamline] Could not update Watch List.", e);
        }
        if (button) {
            button.blur();
        }
    }

    async function togglePlayed(item) {
        const next = !(item.UserData && item.UserData.Played);
        try {
            const data = next
                ? await ApiClient.markPlayed(userId(), item.Id, new Date())
                : await ApiClient.markUnplayed(userId(), item.Id);
            item.UserData = Object.assign({}, item.UserData, data || { Played: next });
            syncPlayedButtons(item);
            invalidate("continue");
            invalidate("mylist");
        } catch (e) {
            console.warn("[Streamline] Could not update watched state.", e);
        }
    }

    // + / check: Watch List. Takes the id of the movie or show (an episode passes its show's).
    function listButton(id, className) {
        const button = el("button", className);
        button.type = "button";
        button.dataset.listFor = id;
        renderListButton(button);
        button.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            toggleWatchlist(id, button);
        });
        return button;
    }

    function renderListButton(button) {
        const inList = inWatchlist(button.dataset.listFor);
        button.replaceChildren(icon(inList ? "check" : "add"));
        if (button.classList.contains("sl-btn")) {
            button.appendChild(el("span", null, "Watch List"));
        }
        button.classList.toggle("is-on", inList);
        button.setAttribute("aria-pressed", String(inList));
        button.title = inList ? "Remove from Watch List" : "Add to Watch List";
        button.setAttribute("aria-label", button.title);
    }

    // Heart: Favorites.
    function favoriteButton(item, className) {
        const button = el("button", `${className} sl-fav-btn`);
        button.type = "button";
        button.dataset.favFor = item.Id;
        renderFavoriteButton(button, item);
        button.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            toggleFavorite(item, button);
        });
        return button;
    }

    function renderFavoriteButton(button, item) {
        const favorite = !!(item.UserData && item.UserData.IsFavorite);
        button.replaceChildren(icon(favorite ? "favorite" : "favorite_border"));
        button.classList.toggle("is-on", favorite);
        button.setAttribute("aria-pressed", String(favorite));
        // On an episode's preview the heart is its show's: name the show so that's clear.
        const what = button.dataset.showName ? `${button.dataset.showName} ` : "";
        button.title = favorite ? `Remove ${what}from Favorites` : `Add ${what}to Favorites`;
        button.setAttribute("aria-label", button.title);
    }

    function playedButton(item, className) {
        const button = el("button", className || "sl-round");
        button.type = "button";
        button.dataset.playedFor = item.Id;
        renderPlayedButton(button, item);
        button.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            togglePlayed(item);
        });
        return button;
    }

    function renderPlayedButton(button, item) {
        const played = !!(item.UserData && item.UserData.Played);
        button.replaceChildren(icon(played ? "remove_done" : "done_all"));
        button.classList.toggle("is-on", played);
        button.title = played ? "Mark as unwatched" : "Mark as watched";
        button.setAttribute("aria-label", button.title);
    }

    function syncPlayedButtons(item) {
        document.querySelectorAll(`[data-played-for="${item.Id}"]`).forEach((button) => renderPlayedButton(button, item));
    }

    // ---------------------------------------------------------------------------------------
    // Ratings: five levels, stored in the user's own Jellyfin item data (UserData.Rating, on
    // Jellyfin's 0-10 scale: 2, 4, 6, 8, 10). Likes is kept in step for other clients. Private:
    // nobody sees anyone else's, and they only shape that user's own recommendations.
    // ---------------------------------------------------------------------------------------

    const RATING_LEVELS = [
        { label: "Terrible", icon: "sentiment_very_dissatisfied", color: "#e5484d" },
        { label: "Bad", icon: "sentiment_dissatisfied", color: "#f76b15" },
        { label: "Okay", icon: "sentiment_neutral", color: "#f5c518" },
        { label: "Good", icon: "sentiment_satisfied", color: "#8bc34a" },
        { label: "Amazing", icon: "sentiment_very_satisfied", color: "#30c46b" }
    ];

    function ratingLevel(item) {
        const rating = item.UserData && item.UserData.Rating;
        return rating > 0 ? Math.min(5, Math.max(1, Math.round(rating / 2))) : 0;
    }

    async function setRating(item, level) {
        const uid = userId();
        await ApiClient.ajax({
            type: "POST",
            url: url(`UserItems/${item.Id}/UserData`, { userId: uid }),
            data: JSON.stringify(level >= 4 || (level && level <= 2)
                ? { Rating: level * 2, Likes: level >= 4 }
                : { Rating: level * 2 }),
            contentType: "application/json"
        });
        if (level === 0 || level === 3) {
            // "Okay" (or no rating) is neither a like nor a dislike.
            await ApiClient.ajax({ type: "DELETE", url: url(`UserItems/${item.Id}/Rating`, { userId: uid }) }).catch(() => null);
        }
        item.UserData = Object.assign({}, item.UserData, { Rating: level * 2 });
        invalidate(`userdata:${item.Id}`);
        document.querySelectorAll(`[data-rate-for="${item.Id}"]`).forEach((button) => renderRateButton(button, item));
    }

    function renderRateButton(button, item) {
        const level = ratingLevel(item);
        const current = RATING_LEVELS[level - 1];
        // The faces icon doesn't explain itself, and phones have no hover hint: until someone
        // has used it once on this device, an unrated button just says "Rate".
        const hint = !current && storageGet(RATE_HINT_KEY) !== "1";
        button.classList.toggle("sl-rate-btn--hint", hint);
        button.replaceChildren(current ? icon(current.icon) : hint ? el("span", "sl-rate-hint", "Rate") : icon("thumbs_up_down"));
        button.classList.toggle("is-on", !!current);
        button.style.setProperty("--sl-rate-color", current ? current.color : "");
        button.title = current ? `Your rating${item.rateOfShow ? " of the show" : ""}: ${current.label}` : item.rateOfShow ? "Rate this show" : "Rate this";
        button.setAttribute("aria-label", button.title);
    }

    let openRatePop = null;

    function closeRatePop() {
        if (openRatePop) {
            openRatePop.remove();
            openRatePop = null;
        }
    }

    document.addEventListener("pointerdown", (event) => {
        if (openRatePop && !event.target.closest(".sl-rate")) {
            closeRatePop();
        }
    }, true);
    document.addEventListener("keydown", (event) => {
        if (event.key === "Escape") {
            closeRatePop();
        }
    });

    // A round button (next to Watch List) that opens five small face buttons above it.
    // Episodes and seasons are rated as their show: the button shows (and sets) the show's
    // rating, and every button for that show follows along.
    function rateTarget(item) {
        if ((item.Type === "Episode" || item.Type === "Season") && item.SeriesId) {
            return { Id: item.SeriesId, Name: item.SeriesName || item.Name, Type: "Series", UserData: null, rateOfShow: true };
        }
        return item;
    }

    function rateButton(original, className) {
        const item = rateTarget(original);
        const wrap = el("span", "sl-rate");
        const button = el("button", `${className || "sl-round"} sl-rate-btn`);
        button.type = "button";
        button.dataset.rateFor = item.Id;
        renderRateButton(button, item);
        if (item.rateOfShow) {
            cached(`userdata:${item.Id}`, 30000, () => getJson(`UserItems/${item.Id}/UserData`, { userId: userId() }))
                .then((data) => {
                    item.UserData = data || {};
                    renderRateButton(button, item);
                })
                .catch(() => { /* stays unrated-looking until opened */ });
        }
        button.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            const wasOpen = openRatePop && wrap.contains(openRatePop);
            closeRatePop();
            if (!wasOpen) {
                if (storageGet(RATE_HINT_KEY) !== "1") {
                    storageSet(RATE_HINT_KEY, "1");
                    document.querySelectorAll(".sl-rate-btn.sl-rate-btn--hint").forEach((other) => other.classList.remove("sl-rate-btn--hint"));
                    document.querySelectorAll(".sl-rate-hint").forEach((hint) => hint.replaceWith(icon("thumbs_up_down")));
                }
                openRatePop = buildRatePop(item, wrap);
                wrap.appendChild(openRatePop);
                const focus = openRatePop.querySelector(".is-on") || openRatePop.querySelector(".sl-rate-option");
                focus.focus({ preventScroll: true });
            }
        });
        wrap.appendChild(button);
        return wrap;
    }

    function buildRatePop(item, wrap) {
        const pop = el("div", "sl-rate-pop");
        pop.setAttribute("role", "radiogroup");
        pop.setAttribute("aria-label", `Rate ${item.Name}`);
        const options = el("div", "sl-rate-options");
        const caption = el("div", "sl-rate-caption");
        const level = ratingLevel(item);
        caption.textContent = level ? RATING_LEVELS[level - 1].label : "How was it?";

        RATING_LEVELS.forEach((entry, index) => {
            const option = el("button", "sl-rate-option");
            option.type = "button";
            option.setAttribute("role", "radio");
            option.setAttribute("aria-checked", String(level === index + 1));
            option.setAttribute("aria-label", entry.label);
            option.title = entry.label;
            option.style.setProperty("--sl-rate-color", entry.color);
            option.classList.toggle("is-on", level === index + 1);
            option.appendChild(icon(entry.icon));
            const show = () => (caption.textContent = entry.label);
            option.addEventListener("mouseenter", show);
            // Only keyboard focus names the face; the focus given when the faces open doesn't.
            option.addEventListener("focus", () => option.matches(":focus-visible") && show());
            option.addEventListener("click", async (event) => {
                event.preventDefault();
                event.stopPropagation();
                // Picking the current rating again takes it away.
                const next = level === index + 1 ? 0 : index + 1;
                closeRatePop();
                try {
                    await setRating(item, next);
                } catch (error) {
                    console.warn("[Streamline] Could not save the rating.", error);
                }
                const button = wrap.querySelector(".sl-rate-btn");
                if (button) {
                    button.focus({ preventScroll: true });
                }
            });
            options.appendChild(option);
        });
        options.addEventListener("mouseleave", () => {
            caption.textContent = level ? RATING_LEVELS[level - 1].label : "How was it?";
        });
        // Arrow keys move between the faces.
        options.addEventListener("keydown", (event) => {
            const buttons = Array.from(options.children);
            const at = buttons.indexOf(document.activeElement);
            if (event.key === "ArrowRight" || event.key === "ArrowLeft") {
                event.preventDefault();
                buttons[(at + (event.key === "ArrowRight" ? 1 : buttons.length - 1)) % buttons.length].focus();
            }
        });

        pop.appendChild(options);
        pop.appendChild(caption);
        return pop;
    }

    // ---------------------------------------------------------------------------------------
    // Data
    // ---------------------------------------------------------------------------------------

    function cached(key, ttl, load) {
        const uid = userId();
        if (state.cacheUserId !== uid) {
            state.cache.clear();
            state.cacheUserId = uid;
        }

        const hit = state.cache.get(key);
        if (hit && Date.now() - hit.at < ttl) {
            return hit.promise;
        }

        const promise = load().catch((error) => {
            state.cache.delete(key);
            throw error;
        });
        state.cache.set(key, { at: Date.now(), promise });
        return promise;
    }

    // Drops a row from every page's cache ("mylist" clears "home:mylist", "lib:...:mylist").
    function invalidate(loader) {
        for (const key of Array.from(state.cache.keys())) {
            if (key === loader || key.endsWith(`:${loader}`)) {
                state.cache.delete(key);
            }
        }
    }

    function loadConfig() {
        const uid = userId();
        if (state.config && state.configUserId === uid) {
            return Promise.resolve(state.config);
        }
        // check() can run several times while this is in flight; share one request.
        if (!state.configPromise) {
            state.configPromise = getJson("Streamline/Config")
                .then((config) => {
                    state.config = config;
                    state.configUserId = uid;
                    storageSet(ENABLED_KEY, config && config.Enabled === false ? "0" : null);
                    return config;
                })
                .finally(() => {
                    state.configPromise = null;
                });
        }
        return state.configPromise;
    }

    async function itemsByIds(ids, fields) {
        if (!ids.length) {
            return [];
        }
        const result = await getJson("Items", {
            UserId: userId(),
            Ids: ids.join(","),
            Fields: fields || ITEM_FIELDS,
            EnableImageTypes: IMAGE_TYPES,
            ImageTypeLimit: 1
        });
        const byId = new Map(((result && result.Items) || []).map((item) => [item.Id, item]));
        // The server doesn't keep the order of Ids; restore it (rank order matters).
        return ids.map((id) => byId.get(id)).filter(Boolean);
    }

    async function queryItems(params) {
        const result = await getJson("Items", Object.assign({
            UserId: userId(),
            Recursive: true,
            IncludeItemTypes: "Movie,Series",
            Fields: ITEM_FIELDS,
            EnableImageTypes: IMAGE_TYPES,
            ImageTypeLimit: 1
        }, params));
        return (result && result.Items) || [];
    }

    let recommendationsRetried = false;
    async function loadRecommendations() {
        let list;
        try {
            list = await getJson(`Recommendations/${userId()}`);
        } catch (e) {
            // Personal Recommendations isn't installed (404) or failed: no row, no error.
            return [];
        }

        if ((!list || !list.length) && !recommendationsRetried) {
            // Its cache is cold right after a restart: it answers empty and warms up in the
            // background. Ask once more shortly after.
            recommendationsRetried = true;
            await new Promise((resolve) => window.setTimeout(resolve, RECOMMENDATIONS_RETRY_MS));
            return loadRecommendations();
        }

        return (list || [])
            .map((entry) => ({ id: entry.Id || entry.id, type: entry.ItemType || entry.itemType || "" }))
            .filter((entry) => entry.id);
    }

    async function loadRecommendedIds(scope) {
        const types = scope.types.split(",");
        return (await loadRecommendations())
            .filter((entry) => !entry.type || types.includes(entry.type))
            .map((entry) => entry.id);
    }

    function limit() {
        return (state.config && state.config.RowItemLimit) || 20;
    }

    // ---------------------------------------------------------------------------------------
    // "New" tags, by air date: a show whose only season is airing is a New Series, an older
    // show with a new season or episodes has New Episodes, each until a month (configurable)
    // after its latest episode aired; a movie is a New Release for two months after release.
    // ---------------------------------------------------------------------------------------

    function loadFreshness() {
        return cached("freshness", STABLE_ROW_TTL_MS, async () => {
            const days = (state.config && state.config.NewSeriesBadgeDays) || 30;
            const result = await getJson("Items", {
                UserId: userId(),
                Recursive: true,
                IncludeItemTypes: "Episode",
                MinPremiereDate: new Date(Date.now() - days * 86400000).toISOString(),
                SortBy: "PremiereDate",
                SortOrder: "Descending",
                Limit: 2000,
                EnableImages: false,
                EnableUserData: false
            });
            const shows = new Map();
            for (const episode of (result && result.Items) || []) {
                const aired = Date.parse(episode.PremiereDate || "") || 0;
                // Specials (season 0) and episodes that haven't aired yet don't count.
                if (!episode.SeriesId || !episode.ParentIndexNumber || aired > Date.now()) {
                    continue;
                }
                const show = shows.get(episode.SeriesId) || { latestSeason: 0 };
                show.latestSeason = Math.max(show.latestSeason, episode.ParentIndexNumber);
                shows.set(episode.SeriesId, show);
            }
            state.freshness = shows;
            return shows;
        });
    }

    // At most one tag per card, by air date: New Release, New Series or New Episodes.
    function badgeFor(item) {
        return airDateBadge(item);
    }

    function airDateBadge(item) {
        if (item.Type === "Movie") {
            const days = (state.config && state.config.NewMovieBadgeDays) || 60;
            const released = Date.parse(item.PremiereDate || "") || 0;
            return released && released <= Date.now() && Date.now() - released <= days * 86400000 ? "New Release" : "";
        }
        const seriesId = item.Type === "Series" ? item.Id : item.Type === "Episode" ? item.SeriesId : null;
        const show = seriesId && state.freshness && state.freshness.get(seriesId);
        if (!show) {
            return "";
        }
        return show.latestSeason > 1 ? "New Episodes" : "New Series";
    }

    // ---------------------------------------------------------------------------------------
    // Scopes: what a page covers. Home spans every library; Movies and Shows pages span one.
    // ---------------------------------------------------------------------------------------

    const HOME_SCOPE = { kind: "home", key: "home", parentId: "", types: "Movie,Series", noun: "Movies & Shows" };

    function scopeFromParams(params) {
        const types = params.get("types") || "Movie,Series";
        const parentId = params.get("parentId") || "";
        return {
            kind: parentId ? "library" : "home",
            key: parentId ? `lib:${parentId}:${types}` : "home",
            parentId,
            types,
            noun: types === "Movie" ? "Movies" : types === "Series" ? "Shows" : "Movies & Shows"
        };
    }

    // Links to the "see all" pages. They live on Jellyfin's own list route, so the back button
    // and deep links work, and genreId/IsFavorite/parentId still mean something to the stock
    // list page if this script ever isn't running.
    function browseHref(scope, params) {
        const query = new URLSearchParams();
        const all = Object.assign({ parentId: scope.parentId, types: scope.types }, params);
        Object.keys(all).forEach((key) => {
            if (all[key]) {
                query.set(key, all[key]);
            }
        });
        try {
            query.set("serverId", ApiClient.serverId());
        } catch (e) {
            // Single-server setups don't need it.
        }
        return `/list?${query.toString()}`;
    }

    // TMDb's TV genres are combined ("Action & Adventure"), so a plain "Action" row would
    // otherwise never show any series.
    // The approved genres, and every name Jellyfin may give them: English and Dutch (TMDb in
    // Dutch says "Actie", "Komedie"...), and TV's combined genres split into their parts. Any
    // other genre ("Kids", "Soap", "Reality", "TV Movie"...) is left out everywhere.
    const GENRE_VARIANTS = {
        Action: ["Action", "Actie"],
        Adventure: ["Adventure", "Avontuur"],
        Comedy: ["Comedy", "Komedie"],
        Crime: ["Crime", "Misdaad"],
        Drama: ["Drama"],
        Fantasy: ["Fantasy", "Fantasie"],
        "Science Fiction": ["Science Fiction", "Sci-Fi", "Sciencefiction", "Science-fiction"],
        Romance: ["Romance", "Romantiek", "Romantisch"],
        // TVDB (which some servers use for shows) says Suspense, Children, Musical and Anime.
        Thriller: ["Thriller", "Suspense"],
        Horror: ["Horror"],
        Mystery: ["Mystery", "Mysterie"],
        Animation: ["Animation", "Animatie", "Anime"],
        Family: ["Family", "Familie", "Children", "Kids"],
        War: ["War", "Oorlog"],
        History: ["History", "Historie", "Historisch", "Geschiedenis"],
        Music: ["Music", "Muziek", "Musical"],
        Western: ["Western"],
        Documentary: ["Documentary", "Documentaire"]
    };

    // Combined TV genres count as each part. "Sci-Fi & Fantasy" isn't queried as Sci-Fi or
    // Fantasy for shows (their keywords decide those, see SERIES_GENRE_TAGS), but it is listed.
    const COMBINED_GENRES = {
        "action & adventure": ["Action", "Adventure"],
        "actie & avontuur": ["Action", "Adventure"],
        "sci-fi & fantasy": ["Science Fiction", "Fantasy"],
        // Mostly politics; shows count as War by their war keywords instead.
        "war & politics": [],
        "oorlog & politiek": []
    };
    const QUERIED_COMBINED = ["Action & Adventure", "Actie & Avontuur"];

    const GENRE_CANONICAL = new Map();
    Object.entries(GENRE_VARIANTS).forEach(([name, variants]) => variants.forEach((variant) => GENRE_CANONICAL.set(variant.toLowerCase(), [name])));
    Object.entries(COMBINED_GENRES).forEach(([combined, parts]) => GENRE_CANONICAL.set(combined, parts));

    // A genre as Jellyfin has it, as the approved genres it stands for ([] for any other).
    function canonicalGenres(raw) {
        return GENRE_CANONICAL.get(String(raw || "").trim().toLowerCase()) || [];
    }

    // A title's genres for showing: approved ones only, by their shown names.
    function shownGenres(item) {
        return Array.from(new Set((item.Genres || []).flatMap(canonicalGenres))).map(genreLabel);
    }

    // TMDb's TV genres have no Romance, Horror or Thriller and lump Fantasy in with Sci-Fi. For
    // shows those come from their TMDb keywords instead (Jellyfin keeps them as tags), which are
    // reliable for these: romance shows are tagged "romance"/"love", fantasy shows "magic",
    // "dragon", "witch"..., sci-fi shows "space", "dystopia", "time travel"... Movies keep using
    // their real genres.
    const SERIES_GENRE_TAGS = {
        romance: ["romance", "love", "romcom", "love story", "romantic comedy", "first love", "love triangle", "forbidden love", "courtship"],
        fantasy: ["magic", "dragon", "fantasy world", "dark fantasy", "high fantasy", "fantasy", "witch", "wizard", "vampire", "sword and sorcery", "based on myths, legends or folklore", "mythology", "fairy tale", "elves"],
        "science fiction": ["space", "space opera", "dystopia", "time travel", "alien", "artificial intelligence (a.i.)", "robot", "post-apocalyptic future", "distant future", "near future", "cyberpunk", "spacecraft", "multiple dimensions", "parallel world", "space travel", "time machine"],
        horror: ["horror", "haunted house", "ghost", "zombie", "slasher", "supernatural horror", "body horror", "demon"],
        thriller: ["thriller", "political thriller", "spy thriller", "psychological thriller", "cyber thriller", "suspense", "conspiracy"],
        // Real wars only: TMDb's plain "war" and "soldier" also cover fantasy battles.
        war: ["world war ii", "world war i", "vietnam war", "american civil war", "korean war", "trench warfare", "d-day", "war on terror", "nazi", "holocaust", "military history", "war drama"],
        history: ["history", "historical", "historical figure", "historical drama", "world history", "ancient rome", "ancient egypt", "ancient greece", "ancient world", "medieval", "middle ages", "vikings", "feudal japan", "16th century", "17th century", "18th century", "19th century"]
    };
    const TAG_GENRE_NAMES = { romance: "Romance", fantasy: "Fantasy", "science fiction": "Science Fiction", horror: "Horror", thriller: "Thriller", war: "War", history: "History" };

    // How genres are shown; the name used in queries stays Jellyfin's.
    const GENRE_LABELS = { "science fiction": "Sci-Fi", documentary: "Documentaries" };

    function genreLabel(name) {
        return GENRE_LABELS[name.toLowerCase()] || name;
    }

    // Every name to ask Jellyfin for when looking up a genre (its variants, and the combined
    // TV genres it's part of).
    function genreNames(name) {
        const canonical = canonicalGenres(name)[0] || name;
        return (GENRE_VARIANTS[canonical] || [name]).concat(QUERIED_COMBINED.filter((combined) => canonicalGenres(combined).includes(canonical)));
    }

    function seriesTags(name) {
        const canonical = canonicalGenres(name)[0] || name;
        return SERIES_GENRE_TAGS[canonical.toLowerCase()] || null;
    }

    function usesTags(scope, name) {
        return !!seriesTags(name) && scope.types.split(",").includes("Series");
    }

    function matchesGenre(item, name) {
        const canonical = canonicalGenres(name)[0] || name;
        const viaSeries = item.Type === "Series" && seriesTags(name);
        // Shows get Sci-Fi and Fantasy from keywords, not from TV's "Sci-Fi & Fantasy".
        if ((item.Genres || []).some((genre) => canonicalGenres(genre).includes(canonical)
            && !(viaSeries && genre.toLowerCase() === "sci-fi & fantasy"))) {
            return true;
        }
        const tags = item.Type === "Series" && seriesTags(name);
        return !!tags && (item.Tags || []).some((tag) => tags.includes(tag.toLowerCase()));
    }

    // Everything in a genre (movies by genre; shows by genre or keywords), up to max of each.
    async function genreItems(scope, name, params, max) {
        const queries = [scopeQuery(scope, Object.assign({ Genres: genreNames(name).join("|"), Limit: max }, params))];
        if (usesTags(scope, name)) {
            queries.push(scopeQuery(scope, Object.assign({ IncludeItemTypes: "Series", Tags: seriesTags(name).join("|"), Limit: max }, params)));
        }
        const seen = new Set();
        return (await Promise.all(queries)).flat().filter((item) => !seen.has(item.Id) && seen.add(item.Id));
    }

    // The genres a page can have rows for: the library's own genres with combined TV genres
    // split up, plus the keyword-based ones when the page has shows.
    async function libraryGenreNames(scope) {
        const result = await getJson("Genres", { UserId: userId(), ParentId: scope.parentId || undefined, IncludeItemTypes: scope.types, Recursive: true, SortBy: "SortName" });
        const names = new Map();
        const add = (name) => names.set(name.toLowerCase(), names.get(name.toLowerCase()) || name);
        // Only the approved genres, under their own names, whatever the library calls them.
        for (const genre of (result && result.Items) || []) {
            canonicalGenres(genre.Name).forEach(add);
        }
        if (scope.types.split(",").includes("Series")) {
            Object.values(TAG_GENRE_NAMES).forEach(add);
        }
        return Array.from(names.values());
    }

    // ---------------------------------------------------------------------------------------
    // Genre rows per person: in the order of what they watch (their taste, worked out by the
    // server from what they watched, favorited and rated), with their own pinned genres first
    // and hidden ones left out. Without any history: the order set by the admin.
    // ---------------------------------------------------------------------------------------

    function loadGenrePrefs() {
        return cached("genre-prefs", STABLE_ROW_TTL_MS, async () => Object.assign({ Auto: true, Pinned: [], Hidden: [] }, await getJson("Streamline/GenrePrefs").catch(() => null)));
    }

    function loadTaste() {
        return cached("taste", STABLE_ROW_TTL_MS, async () => {
            const entries = (await getJson("Streamline/Taste").catch(() => [])) || [];
            const weights = new Map(entries.map((entry) => [normalizeIds([entry.Id])[0], entry.Weight]));
            const ids = Array.from(weights.keys());
            const items = [];
            for (let i = 0; i < ids.length; i += 50) {
                items.push(...await itemsByIds(ids.slice(i, i + 50), `${ITEM_FIELDS},Tags`).catch(() => []));
            }
            return items.map((item) => ({ item, weight: weights.get(normalizeIds([item.Id])[0]) || 0 }));
        });
    }

    // Every genre a page could have a row for, for ordering and for the Customize list.
    async function genreCandidates(scope) {
        const names = new Map();
        const add = (name) => name && !names.has(name.toLowerCase()) && names.set(name.toLowerCase(), name);
        mainGenres().forEach(add);
        String(state.config.HeadlineGenres || "").split(",").map((name) => canonicalGenres(name)[0]).forEach(add);
        (await libraryGenreNames(scope).catch(() => [])).forEach(add);
        if (scope.types === "Series") {
            add("Anime");
        }
        return Array.from(names.values());
    }

    // How much a person watches a genre on this page (movies on Movies, shows on Shows).
    function genreScore(scope, taste, name) {
        const types = scope.types.split(",");
        const def = name === "Anime" ? ANIME : null;
        return taste.reduce((sum, entry) => {
            if (!types.includes(entry.item.Type)) {
                return sum;
            }
            const hit = def ? isAnime(entry.item) : matchesGenre(entry.item, name);
            return hit ? sum + entry.weight : sum;
        }, 0);
    }

    // The genre rows for a page, in order.
    async function genreRowNames(scope) {
        const base = mainGenres();
        const [prefs, taste, candidates] = await Promise.all([loadGenrePrefs(), loadTaste().catch(() => []), genreCandidates(scope)]);
        const lower = (list) => new Set((list || []).map((name) => name.toLowerCase()));
        const hidden = lower(prefs.Hidden);
        const pinned = (prefs.Pinned || []).filter((name) => !hidden.has(name.toLowerCase()) && candidates.some((other) => other.toLowerCase() === name.toLowerCase()));
        const pinnedSet = lower(pinned);
        const count = Math.max(base.length, pinned.length);

        let rest = base.filter((name) => !hidden.has(name.toLowerCase()) && !pinnedSet.has(name.toLowerCase()));
        if (prefs.Auto !== false && taste.length) {
            // What they watch most first (any genre here, not just the admin's list), then the
            // admin's list for the rest; genres they rated down sink.
            const scored = candidates
                .filter((name) => !hidden.has(name.toLowerCase()) && !pinnedSet.has(name.toLowerCase()))
                .map((name) => ({ name, score: genreScore(scope, taste, name) }));
            const liked = scored.filter((entry) => entry.score > 0).sort((x, y) => y.score - x.score).map((entry) => entry.name);
            const disliked = new Set(scored.filter((entry) => entry.score < 0).map((entry) => entry.name.toLowerCase()));
            rest = liked.concat(rest.filter((name) => !liked.includes(name) && !disliked.has(name.toLowerCase())), rest.filter((name) => disliked.has(name.toLowerCase())));
        }
        return pinned.concat(rest).slice(0, count);
    }

    // The admin's genre rows, as approved genres ("Sci-Fi" is Science Fiction).
    function mainGenres() {
        const names = String((state.config && state.config.GenreRows) || "")
            .split(",")
            .map((name) => canonicalGenres(name)[0])
            .filter(Boolean);
        return Array.from(new Set(names));
    }

    function scopeQuery(scope, params) {
        return queryItems(Object.assign({ ParentId: scope.parentId || undefined, IncludeItemTypes: scope.types }, params));
    }

    // Everything added to the server in the Recently Added period: whole new movies and shows, and
    // shows that got new episodes (labelled so you can tell which).
    const newDays = () => (state.config && state.config.RecentlyAddedBadgeDays) || 15;
    async function loadNewItems(scope, max) {
        const cutoff = Date.now() - newDays() * 86400000;
        const types = scope.types.split(",");
        const [movies, series] = await Promise.all([
            types.includes("Movie")
                ? scopeQuery(scope, { IncludeItemTypes: "Movie", SortBy: "DateCreated", SortOrder: "Descending", Limit: max, Fields: `${ITEM_FIELDS},DateCreated` })
                : [],
            types.includes("Series")
                ? scopeQuery(scope, { IncludeItemTypes: "Series", SortBy: "DateLastContentAdded", SortOrder: "Descending", Limit: max, Fields: `${ITEM_FIELDS},DateCreated,DateLastMediaAdded` })
                : []
        ]);

        const entries = [];
        for (const movie of movies) {
            const added = Date.parse(movie.DateCreated || "") || 0;
            if (added >= cutoff) {
                entries.push({ item: movie, date: added });
            }
        }
        for (const show of series) {
            const added = Date.parse(show.DateCreated || "") || 0;
            const latest = Math.max(added, Date.parse(show.DateLastMediaAdded || "") || 0);
            if (latest >= cutoff) {
                entries.push({ item: show, date: latest });
            }
        }
        return entries.sort((x, y) => y.date - x.date).slice(0, max).map((entry) => entry.item);
    }

    // A row needs a few titles to be worth showing; one the person pinned shows from one.
    async function genreRow(scope, name, min = 4) {
        const items = await genreItems(scope, name, { SortBy: "Random" }, limit());
        if (items.length < min) {
            return [];
        }
        return [{
            key: `genre-${name.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`,
            title: genreLabel(name),
            items: items.sort(() => Math.random() - 0.5).slice(0, limit()),
            layout: "landscape",
            seeAll: browseHref(scope, { sl: "genre", genre: name })
        }];
    }

    // Sub-genres. Jellyfin only has flat genres, so these are either two genres together
    // (shows' combined TMDb genres count via genreNames), or TMDb keywords, which Jellyfin keeps
    // as tags. Keywords are noisy ("complex", "bold"), hence a fixed list of useful ones.
    const SUBGENRES = [
        // Two genres together.
        { name: "Sci-Fi Action", genres: ["Science Fiction", "Action"] },
        { name: "Action Thrillers", genres: ["Action", "Thriller"] },
        { name: "Crime Dramas", genres: ["Crime", "Drama"] },
        { name: "Crime Thrillers", genres: ["Crime", "Thriller"] },
        { name: "Romantic Comedies", genres: ["Romance", "Comedy"] },
        { name: "Romantic Dramas", genres: ["Romance", "Drama"] },
        { name: "Fantasy Adventures", genres: ["Fantasy", "Adventure"] },
        { name: "Family Animation", genres: ["Animation", "Family"] },
        // Themes, from TMDb keywords.
        { name: "Musicals", tags: ["musical", "musical film", "broadway", "song and dance", "jukebox musical"] },
        { name: "Based on a True Story", tags: ["based on true story", "based on real events", "true story", "biography", "biopic", "biographical"] },
        { name: "Superheroes", tags: ["superhero", "superhero team", "marvel cinematic universe (mcu)", "dc extended universe (dceu)"] },
        { name: "Period Dramas", tags: ["period drama", "period piece", "georgian or regency era", "victorian england", "19th century", "18th century", "regency", "historical drama"] },
        { name: "Sitcoms", tags: ["sitcom", "workplace comedy", "mockumentary", "ensemble cast"] },
        { name: "Feel-Good", tags: ["feel-good", "heartwarming", "wholesome", "uplifting", "hopeful", "friendship"] },
        { name: "Dystopian", tags: ["dystopia", "dystopian", "post-apocalyptic future", "post-apocalyptic", "totalitarianism"] },
        { name: "Space", tags: ["space", "outer space", "spacecraft", "space travel", "astronaut", "space opera"] },
        { name: "Psychological Thrillers", tags: ["psychological thriller", "psychological", "psychopath", "mind game", "obsession"] },
        { name: "Cops & Detectives", tags: ["police", "police detective", "cop", "police investigation", "detective", "fbi", "serial killer", "true crime"] },
        { name: "Whodunits", tags: ["whodunit", "murder mystery", "murder investigation"] },
        { name: "Legal Dramas", tags: ["lawyer", "courtroom", "legal drama", "trial", "court case", "attorney"] },
        { name: "Medical Dramas", tags: ["hospital", "doctor", "medical drama", "surgeon", "nurse"] },
        { name: "Political Dramas", tags: ["politics", "political thriller", "election", "president", "political drama", "white house"] },
        { name: "Dark Comedies", tags: ["dark comedy", "black comedy", "satire"] },
        { name: "Coming of Age", tags: ["coming of age", "high school", "teenager", "teen drama", "first love"] },
        { name: "Sports", tags: ["sports", "sport", "football (soccer)", "boxing", "basketball", "baseball", "american football", "underdog"] },
        { name: "Epic Fantasy", tags: ["high fantasy", "fantasy world", "sword and sorcery", "dragon", "epic battle", "magic", "wizard", "mythology", "fairy tale"] },
        { name: "Supernatural", tags: ["supernatural", "ghost", "demon", "paranormal", "haunted house", "possession", "vampire", "zombie", "monster"] },
        { name: "Christmas & Holidays", tags: ["christmas", "holiday", "christmas eve", "santa claus", "halloween"] },
        // Documentaries about nature: the Documentary genre and a nature keyword, both.
        { name: "Nature Documentaries", genres: ["Documentary"], tags: ["nature", "wildlife", "animals", "nature documentary", "ocean", "planet earth", "environment", "animal behavior", "natural history", "wilderness"] },
        // Anime: the Anime genre (TVDB), or an animation with an anime keyword (TMDb).
        { name: "Anime", tags: ["anime", "shounen", "seinen", "shoujo", "josei", "isekai", "based on manga", "based on light novel", "donghua"], genres: ["Animation"], anime: true }
    ];

    const ANIME = SUBGENRES.find((def) => def.name === "Anime");

    function isAnime(item) {
        const genres = (item.Genres || []).map((genre) => genre.toLowerCase());
        return genres.includes("anime") || (matchesGenre(item, "Animation") && (item.Tags || []).some((tag) => ANIME.tags.includes(tag.toLowerCase())));
    }

    // Everything in a sub-genre (up to max), cached per page so its row, its tile on the genres
    // page and its own page share one fetch.
    function subgenreItems(scope, def, max) {
        return cached(`${scope.key}:sub:${def.name}`, STABLE_ROW_TTL_MS, async () => {
            if (def.anime) {
                const [tagged, genre] = await Promise.all([
                    scopeQuery(scope, { Tags: def.tags.join("|"), SortBy: "SortName", Limit: Math.max(max, 500), Fields: `${ITEM_FIELDS},Tags` }),
                    scopeQuery(scope, { Genres: "Anime", SortBy: "SortName", Limit: max, Fields: `${ITEM_FIELDS},Tags` })
                ]);
                const seen = new Set();
                return genre.concat(tagged.filter(isAnime))
                    .filter((item) => !seen.has(item.Id) && seen.add(item.Id))
                    .sort((x, y) => String(x.SortName || x.Name).localeCompare(String(y.SortName || y.Name)))
                    .slice(0, max);
            }
            if (def.tags) {
                // A keyword, plus a genre when there is one (Nature Documentaries: a nature
                // keyword on a documentary).
                const tagged = await scopeQuery(scope, { Tags: def.tags.join("|"), SortBy: "SortName", Limit: def.genres ? Math.max(max, 500) : max, Fields: `${ITEM_FIELDS},Tags` });
                return def.genres ? tagged.filter((item) => def.genres.every((genre) => matchesGenre(item, genre))).slice(0, max) : tagged;
            }
            // Both genres at once: the API only does "either", so fetch the first and keep the
            // ones that also have the second.
            const candidates = await genreItems(scope, def.genres[0], { SortBy: "SortName", Fields: `${ITEM_FIELDS},Tags` }, Math.max(max, 500));
            return candidates.filter((item) => matchesGenre(item, def.genres[1])).slice(0, max);
        });
    }

    async function subgenreRow(scope, def, min = 4) {
        const items = await subgenreItems(scope, def, 500);
        if (items.length < min) {
            return [];
        }
        return [{
            key: `sub-${def.name.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`,
            title: def.name,
            // Random pick per visit, like the genre rows; the cached list stays A-Z.
            items: items.slice().sort(() => Math.random() - 0.5).slice(0, limit()),
            layout: "landscape",
            seeAll: browseHref(scope, { sl: "sub", sub: def.name })
        }];
    }

    // The Watch List's movies and shows for a page, newest additions first. The server already
    // dropped anything watched since it was added.
    async function watchlistItems(scope) {
        const ids = await loadWatchlist();
        const types = scope.types.split(",");
        const items = [];
        for (let i = 0; i < ids.length; i += 50) {
            items.push(...await itemsByIds(ids.slice(i, i + 50)));
        }
        return items.filter((item) => types.includes(item.Type));
    }

    function comingDateLabel(entry) {
        if (!entry.Date) {
            return "On its way";
        }
        const date = new Date(`${entry.Date}T12:00:00`);
        const today = new Date();
        today.setHours(12, 0, 0, 0);
        const days = Math.round((date - today) / 86400000);
        if (days <= 0) {
            return "Today";
        }
        if (days === 1) {
            return "Tomorrow";
        }
        return date.toLocaleDateString(undefined, days < 7 ? { weekday: "long" } : { weekday: "short", day: "numeric", month: "short" });
    }

    // A card for something that isn't (fully) here yet: artwork from Jellyfin when the show is
    // in the library, else from TMDb (via Seerr); the date it lands as the tag.
    function buildComingCard(entry) {
        const layout = COMPACT_QUERY.matches ? "poster" : "landscape";
        // Only a show that's on the server opens (its own page); anything not here yet is just
        // a picture with its date.
        const card = el(entry.ItemId ? "a" : "div", `sl-card sl-card--${layout} sl-card--coming${entry.ItemId ? "" : " sl-card--static"}`);
        if (entry.ItemId) {
            card.href = `#/details?id=${entry.ItemId}`;
        }
        card.setAttribute("aria-label", `${entry.Title}, ${entry.Subtitle}, ${comingDateLabel(entry)}`);
        const media = el("div", "sl-card-media");
        let src = "";
        if (entry.ItemId) {
            src = imageUrl(entry.ItemId, layout === "poster" ? "Primary" : "Backdrop", undefined, layout === "poster" ? { maxHeight: 480 } : { maxWidth: 560 });
        } else if (layout === "poster" ? entry.Poster : entry.Backdrop || entry.Poster) {
            src = `https://image.tmdb.org/t/p/${layout === "poster" ? "w342" : "w780"}${layout === "poster" ? entry.Poster : entry.Backdrop || entry.Poster}`;
        }
        if (src) {
            media.appendChild(lazyImage(src, "sl-card-img"));
        }
        if (layout === "landscape") {
            const treatment = el("div", "sl-card-treatment");
            const name = el("span", "sl-card-name", entry.Title);
            treatment.appendChild(name);
            if (entry.ItemId) {
                const logo = lazyImage(imageUrl(entry.ItemId, "Logo", undefined, { maxWidth: 300 }), "sl-card-logo");
                logo.addEventListener("load", () => name.remove(), { once: true });
                treatment.insertBefore(logo, name);
            }
            media.appendChild(treatment);
        }
        media.appendChild(el("span", "sl-card-badge sl-card-badge--date", comingDateLabel(entry)));
        card.appendChild(media);
        card.appendChild(el("div", "sl-card-caption", entry.Subtitle));
        if (entry.ItemId) {
            card.addEventListener("click", (event) => {
                event.preventDefault();
                showDetails({ Id: entry.ItemId });
            });
        }
        return card;
    }

    const rowLoaders = {
        async continue() {
            const uid = userId();
            const common = { UserId: uid, Fields: ITEM_FIELDS, EnableImageTypes: IMAGE_TYPES, ImageTypeLimit: 1 };
            const [resume, nextUp] = await Promise.all([
                getJson("UserItems/Resume", Object.assign({ Limit: limit(), MediaTypes: "Video" }, common)).catch(() => null),
                getJson("Shows/NextUp", Object.assign({ Limit: limit(), EnableResumable: false, EnableRewatching: false }, common)).catch(() => null)
            ]);

            // One merged row, like on streaming services: half-watched things first, then the
            // next episode of every show in progress that isn't already represented.
            const items = (resume && resume.Items) || [];
            const seriesSeen = new Set(items.map((item) => item.SeriesId).filter(Boolean));
            for (const item of (nextUp && nextUp.Items) || []) {
                if (!seriesSeen.has(item.SeriesId)) {
                    seriesSeen.add(item.SeriesId);
                    items.push(item);
                }
            }
            return [{ key: "continue", title: "Continue Watching", items: items.slice(0, limit()), layout: "landscape", action: "play" }];
        },

        async recommended(scope) {
            const ids = await loadRecommendedIds(scope);
            const items = await itemsByIds(ids.slice(0, limit()));
            return [{ key: "recommended", title: "Recommended For You", items, layout: "landscape", seeAll: browseHref(scope, { sl: "recommended" }) }];
        },

        async newest(scope) {
            let items = await loadNewItems(scope, limit());
            if (items.length < 4) {
                // A quiet month: show the latest additions regardless of age rather than nothing.
                const result = await getJson("Items/Latest", {
                    UserId: userId(),
                    ParentId: scope.parentId || undefined,
                    Limit: limit(),
                    IncludeItemTypes: scope.types.includes("Series") ? `${scope.types},Episode` : scope.types,
                    GroupItems: true,
                    Fields: ITEM_FIELDS,
                    EnableImageTypes: IMAGE_TYPES,
                    ImageTypeLimit: 1
                });
                // Grouped episodes come back as their series, sometimes more than once.
                const seen = new Set();
                items = (result || []).filter((item) => {
                    const key = item.SeriesId || item.Id;
                    if (seen.has(key)) {
                        return false;
                    }
                    seen.add(key);
                    return true;
                });
            }
            return [{ key: "new", title: "New on Jellyfin", items, layout: "landscape", seeAll: browseHref(scope, { sl: "new" }) }];
        },

        async comingSoon(scope) {
            if (!state.config.HasSeerr) {
                return [];
            }
            // Only episodes about to air, so nothing for the Movies page.
            if (scope.types === "Movie") {
                return [];
            }
            const items = (await getJson("Streamline/ComingSoon").catch(() => [])) || [];
            return [{ key: "coming", title: "Coming Soon", items, layout: "landscape", cardBuilder: buildComingCard }];
        },

        async mylist(scope) {
            const items = (await watchlistItems(scope)).slice(0, limit());
            return [{ key: "mylist", title: "Watch List", items, layout: "landscape", seeAll: browseHref(scope, { sl: "mylist" }) }];
        },

        async favorites(scope) {
            const items = await scopeQuery(scope, { Filters: "IsFavorite", SortBy: "DatePlayed,SortName", SortOrder: "Descending", Limit: limit() });
            return [{ key: "favorites", title: "Favorites", items, layout: "landscape", seeAll: browseHref(scope, { sl: "favorites", IsFavorite: "true" }) }];
        },

        // Home: the featured collections and franchises as poster tiles.
        async collections() {
            // The person's pinned collections first; nothing they hid.
            const [collections, prefs] = await Promise.all([loadCollections(), loadCollectionPrefs()]);
            const { pinned, rest } = visibleCollections(collections, prefs);
            const list = pinned.concat(rest.filter((collection) => !isBroadCollection(collection) && ["featured", "universe"].includes(collection.Kind)));
            const tiles = await collectionTiles(list.slice(0, limit()));
            return [{
                key: "collections",
                title: "Collections",
                items: tiles,
                layout: "poster",
                seeAll: collectionsHref(),
                cardBuilder: (tile) => {
                    const card = el("div", "sl-card sl-card--poster sl-card--collection");
                    card.appendChild(buildGenreTile(tile));
                    return card;
                }
            }];
        },

        async acclaimed(scope) {
            const items = await scopeQuery(scope, { MinCommunityRating: ACCLAIMED_MIN_RATING, SortBy: "CriticRating,CommunityRating", SortOrder: "Descending", Limit: limit() });
            return [{ key: "acclaimed", title: "Critically Acclaimed", items, layout: "landscape", seeAll: browseHref(scope, { sl: "acclaimed" }) }];
        },

        async genres(scope) {
            // In this person's order (their pins, then what they watch most).
            const names = await genreRowNames(scope);
            const prefs = await loadGenrePrefs();
            const pinned = new Set((prefs.Pinned || []).map((name) => name.toLowerCase()));
            const loaders = names.map((name) => {
                const min = pinned.has(name.toLowerCase()) ? 1 : 4;
                return () => (name === "Anime" ? subgenreRow(scope, ANIME, min) : genreRow(scope, name, min));
            });
            // Shows also get Anime right under Action, unless it's already placed or hidden.
            const hidden = (prefs.Hidden || []).some((name) => name.toLowerCase() === "anime");
            if (scope.types === "Series" && !names.includes("Anime") && !hidden) {
                const at = names.findIndex((name) => name.toLowerCase() === "action");
                loaders.splice(at + 1 || loaders.length, 0, () => subgenreRow(scope, ANIME));
            }
            const rows = (await Promise.all(loaders.map((load) => load()))).flat();
            if (rows.length) {
                rows[0] = Object.assign({}, rows[0], { customizeBar: () => openGenreCustomizer(scope) });
            }
            return rows;
        }
    };

    const ACCLAIMED_MIN_RATING = 7.5;

    // Every other genre in the library, one lazily loaded row each, after the main ones.
    async function otherGenreSlots(scope) {
        const main = new Set(mainGenres().map((name) => name.toLowerCase()));
        return (await libraryGenreNames(scope))
            .filter((name) => !main.has(name.toLowerCase()))
            .map((name) => ({
                key: `${scope.key}:genre:${name}`,
                ttl: STABLE_ROW_TTL_MS,
                lazy: true,
                load: () => genreRow(scope, name)
            }));
    }

    // Which rows a page has, top to bottom. ttl 0 = re-fetched every time the page is shown.
    function slotsFor(scope) {
        const slot = (loader, ttl) => ({ key: `${scope.key}:${loader}`, ttl, load: () => rowLoaders[loader](scope) });
        if (scope.kind === "home") {
            return [
                slot("continue", 0),
                slot("recommended", STABLE_ROW_TTL_MS),
                slot("newest", STABLE_ROW_TTL_MS),
                slot("comingSoon", STABLE_ROW_TTL_MS),
                slot("mylist", 0),
                slot("favorites", 0),
                slot("collections", STABLE_ROW_TTL_MS),
                slot("genres", STABLE_ROW_TTL_MS)
            ];
        }
        return [
            slot("mylist", 0),
            slot("favorites", 0),
            slot("comingSoon", STABLE_ROW_TTL_MS),
            slot("recommended", STABLE_ROW_TTL_MS),
            slot("acclaimed", STABLE_ROW_TTL_MS),
            slot("genres", STABLE_ROW_TTL_MS),
            ...SUBGENRES.filter((def) => !(def === ANIME && scope.types === "Series")).map((def) => ({ key: `${scope.key}:subrow:${def.name}`, ttl: STABLE_ROW_TTL_MS, lazy: true, load: () => subgenreRow(scope, def) })),
            { key: `${scope.key}:other-genres`, expand: () => cached(`${scope.key}:other-genres`, STABLE_ROW_TTL_MS, () => otherGenreSlots(scope)) }
        ];
    }

    function loadSlot(slot) {
        // Cards need the "new" tag data when they're built, so rows wait for it (one shared,
        // cached request); if it fails, rows still show, just without tags.
        return Promise.all([loadFreshness().catch(() => null), loadWatchlist().catch(() => null), cached(slot.key, slot.ttl, slot.load)])
            .then(([, , rows]) => rows);
    }

    // ---------------------------------------------------------------------------------------
    // "See all" pages: one title's worth of a row, as a grid
    // ---------------------------------------------------------------------------------------

    // Sorts on "see all" pages. Each has a natural direction; the direction button flips it
    // (newest/oldest first, highest/lowest first, A-Z/Z-A).
    const BROWSE_SORTS = [
        { key: "match", label: "Best match", desc: false, labels: ["Best first", "Reversed"] },
        { key: "order", label: "Release order", desc: false, labels: ["Oldest first", "Newest first"] },
        { key: "listed", label: "Added to list", desc: false, labels: ["Newest first", "Oldest first"] },
        { key: "rating", label: "Top rated", sortBy: "CommunityRating,SortName", desc: true, labels: ["Highest first", "Lowest first"] },
        { key: "added", label: "Recently added", sortBy: "DateCreated,SortName", desc: true, labels: ["Newest first", "Oldest first"] },
        { key: "released", label: "Release date", sortBy: "PremiereDate,SortName", desc: true, labels: ["Newest first", "Oldest first"] },
        { key: "az", label: "Title", sortBy: "SortName", desc: false, labels: ["A–Z", "Z–A"] }
    ];

    const SORT_VALUES = {
        rating: (item) => item.CommunityRating || 0,
        added: (item) => Date.parse(item.DateLastMediaAdded || item.DateCreated || "") || 0,
        released: (item) => Date.parse(item.PremiereDate || "") || 0
    };

    // Sorting for lists that come back whole rather than as a server query.
    function sortLocal(items, sort, desc) {
        if (sort.key === "match" || sort.key === "listed" || sort.key === "order") {
            return desc ? items.slice().reverse() : items.slice();
        }
        const sorted = items.slice().sort((x, y) => {
            if (sort.key === "az") {
                return (x.SortName || x.Name || "").localeCompare(y.SortName || y.Name || "");
            }
            return SORT_VALUES[sort.key](x) - SORT_VALUES[sort.key](y)
                || (x.SortName || x.Name || "").localeCompare(y.SortName || y.Name || "");
        });
        return desc ? sorted.reverse() : sorted;
    }

    async function pagedQuery(scope, params, start, count) {
        const result = await getJson("Items", Object.assign({
            UserId: userId(),
            Recursive: true,
            ParentId: scope.parentId || undefined,
            IncludeItemTypes: scope.types,
            Fields: ITEM_FIELDS,
            EnableImageTypes: IMAGE_TYPES,
            ImageTypeLimit: 1,
            StartIndex: start,
            Limit: count
        }, params));
        return { items: (result && result.Items) || [], total: (result && result.TotalRecordCount) || 0 };
    }

    const STANDARD_SORTS = ["rating", "added", "released", "az"];

    // Describes a "see all" page from its URL: heading, sorts, and how to fetch a page of results
    // (or the whole list at once, for lists that aren't a plain server query).
    function browseSource(params) {
        const scope = scopeFromParams(params);
        const kind = params.get("sl");
        const query = (base) => (start, count, sort, desc) => pagedQuery(scope, Object.assign({ SortBy: sort.sortBy, SortOrder: desc ? "Descending" : "Ascending" }, base), start, count);
        const standard = (extra) => Object.assign({ scope, sorts: STANDARD_SORTS }, extra);

        switch (kind) {
            case "genre": {
                const name = params.get("genre") || "";
                // Shows matched by keyword can't be paged together with a genre query, so those
                // genres come back whole (and are sorted here).
                return usesTags(scope, name)
                    ? standard({ title: genreLabel(name), defaultSort: "rating", all: () => genreItems(scope, name, { SortBy: "SortName" }, 1000) })
                    : standard({ title: genreLabel(name), defaultSort: "rating", page: query({ Genres: genreNames(name).join("|") }) });
            }
            case "sub": {
                const def = SUBGENRES.find((entry) => entry.name === params.get("sub"));
                return def ? standard({ title: def.name, defaultSort: "rating", all: () => subgenreItems(scope, def, 1000) }) : null;
            }
            case "collection":
                return {
                    scope,
                    title: "Collection",
                    sorts: ["order"].concat(STANDARD_SORTS),
                    defaultSort: "order",
                    empty: "Nothing from this collection is on the server right now.",
                    // The name is only known once the list is in; the page fills it in.
                    collectionId: params.get("id") || "",
                    all: async () => {
                        const collection = (await loadCollections()).find((entry) => entry.Id === params.get("id"));
                        return collection ? collectionMembers(collection) : [];
                    }
                };
            case "acclaimed":
                return standard({ title: "Critically Acclaimed", defaultSort: "rating", page: query({ MinCommunityRating: ACCLAIMED_MIN_RATING }) });
            case "mylist":
                return {
                    scope,
                    title: "Watch List",
                    subtitle: "Still to watch",
                    empty: "Your Watch List is empty. Press + on anything you want to watch later.",
                    sorts: ["listed"].concat(STANDARD_SORTS),
                    defaultSort: "listed",
                    all: () => watchlistItems(scope)
                };
            case "favorites":
                return standard({ title: "Favorites", defaultSort: "added", empty: "No favorites yet. Press \u2665 on a movie or show you love.", page: query({ Filters: "IsFavorite" }) });
            case "all":
                return standard({ title: `All ${scope.noun}`, defaultSort: "az", page: query({}) });
            case "new":
                return standard({ title: "New on Jellyfin", subtitle: `Added in the last ${newDays()} days`, defaultSort: "added", all: () => loadNewItems(scope, 500) });
            case "recommended":
                return {
                    scope,
                    title: "Recommended For You",
                    empty: "Nothing to recommend yet. Watch or rate a few things and check back.",
                    sorts: ["match"].concat(STANDARD_SORTS),
                    defaultSort: "match",
                    all: async () => {
                        const ids = (await loadRecommendedIds(scope)).slice(0, 200);
                        const items = [];
                        for (let i = 0; i < ids.length; i += 50) {
                            items.push(...await itemsByIds(ids.slice(i, i + 50)));
                        }
                        return items;
                    }
                };
            default:
                return null;
        }
    }

    // Everything the All Genres page shows, in order: the main genres, the sub-genres, then
    // every other genre in the library, most titles first. Each with a few posters and a count.
    const tilePosters = (items) => items.filter((item) => item.ImageTags && item.ImageTags.Primary).slice(0, 4);
    const byRating = (items) => items.slice().sort((x, y) => (y.CommunityRating || 0) - (x.CommunityRating || 0));

    async function genreTileData(scope, name) {
        const href = browseHref(scope, { sl: "genre", genre: name });
        if (usesTags(scope, name)) {
            const items = await genreItems(scope, name, { SortBy: "CommunityRating", SortOrder: "Descending" }, 500);
            return { name: genreLabel(name), href, items: tilePosters(byRating(items)), total: items.length };
        }
        const result = await pagedQuery(scope, { Genres: genreNames(name).join("|"), SortBy: "CommunityRating", SortOrder: "Descending", ImageTypes: "Primary" }, 0, 8);
        return { name: genreLabel(name), href, items: tilePosters(result.items), total: result.total };
    }

    async function subTileData(scope, def) {
        // Keyword-only sub-genres: the count and a few posters straight from the server, instead
        // of fetching every title just to count them.
        if (def.tags && !def.genres) {
            const result = await pagedQuery(scope, { Tags: def.tags.join("|"), SortBy: "CommunityRating", SortOrder: "Descending", ImageTypes: "Primary" }, 0, 8);
            return { name: def.name, href: browseHref(scope, { sl: "sub", sub: def.name }), items: tilePosters(result.items), total: result.total };
        }
        const items = await subgenreItems(scope, def, 500);
        return { name: def.name, href: browseHref(scope, { sl: "sub", sub: def.name }), items: tilePosters(byRating(items)), total: items.length };
    }

    async function genreTiles(scope) {
        const genreTile = (name) => genreTileData(scope, name);

        const mainNames = String(state.config.HeadlineGenres || state.config.GenreRows || "")
            .split(",")
            .map((name) => name.trim())
            .filter(Boolean);
        const main = await Promise.all(mainNames.map(genreTile));

        const subs = await Promise.all(SUBGENRES.map((def) => subTileData(scope, def)));

        const mainSet = new Set(mainNames.map((name) => name.toLowerCase()));
        const rest = await Promise.all((await libraryGenreNames(scope))
            .filter((name) => !mainSet.has(name.toLowerCase()))
            .map(genreTile));

        // The headline genres big, then sub-genres and every other genre as one run of smaller
        // tiles: no sub-headings, the size difference does the separating.
        return [
            { headline: true, tiles: main.filter((tile) => tile.total > 0) },
            {
                tiles: subs.filter((tile) => tile.total >= 4)
                    .concat(rest.filter((tile) => tile.total > 0).sort((x, y) => y.total - x.total))
            }
        ].filter((section) => section.tiles.length);
    }

    // ---------------------------------------------------------------------------------------
    // Cards
    // ---------------------------------------------------------------------------------------

    function cardLayout(row) {
        if (row.layout === "poster") {
            return "poster";
        }
        return COMPACT_QUERY.matches && row.action !== "play" ? "poster" : "landscape";
    }

    // The last card in a row with a "see all" page: same size as the others, so it reads as
    // "there's more this way" rather than a separate button.
    function buildMoreCard(row) {
        const card = el("a", `sl-card sl-card--${cardLayout(row)} sl-card--more`);
        card.href = `#${row.seeAll}`;
        card.setAttribute("aria-label", `See all: ${row.title}`);
        const media = el("div", "sl-card-media");
        const inner = el("div", "sl-more-inner");
        inner.appendChild(icon("arrow_forward"));
        inner.appendChild(el("span", "sl-more-label", "See all"));
        inner.appendChild(el("span", "sl-more-title", row.title));
        media.appendChild(inner);
        card.appendChild(media);
        card.addEventListener("click", (event) => {
            event.preventDefault();
            navigate(row.seeAll);
        });
        return card;
    }

    function buildCard(item, row, index) {
        const layout = cardLayout(row);

        const card = el("a", `sl-card sl-card--${layout}`);
        card.href = `#/details?id=${item.Id}`;
        card.dataset.id = item.Id;
        const label = item.Type === "Episode" ? `${item.SeriesName}, ${episodeLabel(item)}` : item.Name;
        card.setAttribute("aria-label", row.action === "play" ? `Play ${label}` : label);


        const media = el("div", "sl-card-media");
        const src = layout === "landscape" ? landscapeUrl(item, 560) : posterUrl(item, 480);
        if (src) {
            media.appendChild(lazyImage(src, "sl-card-img"));
        }

        if (layout === "landscape") {
            const logo = logoUrl(item, 300);
            const treatment = el("div", "sl-card-treatment");
            if (logo) {
                treatment.appendChild(lazyImage(logo, "sl-card-logo"));
            } else {
                treatment.appendChild(el("span", "sl-card-name", item.Type === "Episode" ? item.SeriesName : item.Name));
            }
            media.appendChild(treatment);
        } else if (!src) {
            media.appendChild(el("span", "sl-card-name sl-card-name--center", item.Name));
        }

        const badge = badgeFor(item);
        if (badge) {
            media.appendChild(el("span", "sl-card-badge", badge));
        }

        if (row.action === "play") {
            const playBadge = el("span", "sl-card-play");
            playBadge.appendChild(icon("play_arrow"));
            media.appendChild(playBadge);
        }

        const percent = progressPercent(item);
        if (percent > 0 && !(item.UserData && item.UserData.Played)) {
            const bar = el("div", "sl-progress");
            const fill = el("span");
            fill.style.width = `${percent}%`;
            bar.appendChild(fill);
            media.appendChild(bar);
        }

        card.appendChild(media);

        if (row.action === "play" && item.Type === "Episode") {
            card.appendChild(el("div", "sl-card-caption", episodeLabel(item)));
        }

        card.addEventListener("click", (event) => {
            event.preventDefault();
            if (row.action === "play") {
                play(item);
            } else {
                showDetails(item);
            }
        });

        cardData.set(card, item);
        card.addEventListener("mouseenter", () => schedulePreview(card, item));
        card.addEventListener("mouseleave", cancelPreviewTimer);

        return card;
    }

    // ---------------------------------------------------------------------------------------
    // Hover preview (mouse only)
    // ---------------------------------------------------------------------------------------

    let previewTimer = 0;
    const cardData = new WeakMap();

    function schedulePreview(card, item) {
        if (!HOVER_QUERY.matches) {
            return;
        }
        cancelPreviewTimer();
        previewTimer = window.setTimeout(() => openPreview(card, item), PREVIEW_DELAY_MS);
    }

    function cancelPreviewTimer() {
        window.clearTimeout(previewTimer);
        previewTimer = 0;
    }

    function openPreview(card, item) {
        if (!card.isConnected) {
            return;
        }
        closePreview(true);

        const rect = card.querySelector(".sl-card-media").getBoundingClientRect();
        const gutter = 12;
        const width = Math.max(320, Math.min(rect.width * 1.5, 440));
        const left = Math.min(Math.max(rect.left + rect.width / 2 - width / 2, gutter), window.innerWidth - width - gutter);
        const mediaHeight = width * 9 / 16;
        const top = rect.top + window.scrollY + rect.height / 2 - mediaHeight / 2;
        const origin = rect.left + rect.width / 2 - left;

        const preview = el("div", "sl-preview");
        preview.style.width = `${width}px`;
        preview.style.left = `${left + window.scrollX}px`;
        preview.style.top = `${Math.max(top, window.scrollY + gutter)}px`;
        preview.style.transformOrigin = `${origin}px ${mediaHeight / 2}px`;
        preview.setAttribute("role", "dialog");
        preview.setAttribute("aria-label", item.Name);

        const media = el("button", "sl-preview-media");
        media.type = "button";
        media.setAttribute("aria-label", `More info about ${item.Name}`);
        const src = landscapeUrl(item, 720);
        if (src) {
            media.appendChild(lazyImage(src, "sl-card-img"));
        }
        const logo = logoUrl(item, 360);
        const treatment = el("div", "sl-card-treatment");
        if (logo) {
            treatment.appendChild(lazyImage(logo, "sl-card-logo"));
        } else {
            treatment.appendChild(el("span", "sl-card-name", item.Type === "Episode" ? item.SeriesName : item.Name));
        }
        media.appendChild(treatment);
        media.addEventListener("click", () => showDetails(item));

        const body = el("div", "sl-preview-body");
        // The title in plain text, right under the picture (logos can be hard to read).
        body.appendChild(el("div", "sl-preview-title", item.Type === "Episode" ? item.SeriesName || item.Name : item.Name));

        const actions = el("div", "sl-preview-actions");
        const playButton = el("button", "sl-round sl-round--primary");
        playButton.type = "button";
        playButton.title = "Play";
        playButton.setAttribute("aria-label", `Play ${item.Name}`);
        playButton.appendChild(icon("play_arrow"));
        playButton.addEventListener("click", () => play(item));
        actions.appendChild(playButton);
        if (item.Type !== "Episode") {
            actions.appendChild(listButton(item.Id, "sl-round"));
            actions.appendChild(favoriteButton(item, "sl-round"));
            actions.appendChild(rateButton(item));
        } else if (item.SeriesId) {
            // An episode (Continue Watching): the buttons act on its show. The episode doesn't
            // carry the show's favorite/rating, so those appear once the show's been looked up.
            actions.appendChild(listButton(item.SeriesId, "sl-round"));
            const holder = el("span", "sl-list-slot");
            actions.appendChild(holder);
            ApiClient.getItem(userId(), item.SeriesId)
                .then((series) => {
                    if (holder.isConnected) {
                        const heart = favoriteButton(series, "sl-round");
                        heart.dataset.showName = series.Name;
                        renderFavoriteButton(heart, series);
                        holder.replaceWith(heart, rateButton(series));
                    }
                })
                .catch(() => holder.remove());
        }
        actions.appendChild(playedButton(item));
        actions.appendChild(el("span", "sl-spacer"));
        const moreButton = el("button", "sl-round");
        moreButton.type = "button";
        moreButton.title = "More info";
        moreButton.setAttribute("aria-label", "More info");
        moreButton.appendChild(icon("expand_more"));
        moreButton.addEventListener("click", () => showDetails(item));
        actions.appendChild(moreButton);
        body.appendChild(actions);

        if (item.Type === "Episode") {
            body.appendChild(el("div", "sl-preview-episode", episodeLabel(item)));
        }

        body.appendChild(buildMetaLine(item));

        const percent = progressPercent(item);
        if (percent > 0 && !(item.UserData && item.UserData.Played) && item.RunTimeTicks) {
            const progress = el("div", "sl-preview-progress");
            const bar = el("div", "sl-progress sl-progress--inline");
            const fill = el("span");
            fill.style.width = `${percent}%`;
            bar.appendChild(fill);
            progress.appendChild(bar);
            const left = Math.max(1, Math.round((item.RunTimeTicks - item.UserData.PlaybackPositionTicks) / 600000000));
            progress.appendChild(el("span", null, `${left}m left`));
            body.appendChild(progress);
        }

        if (item.Genres && item.Genres.length) {
            const genres = shownGenres(item);
            if (genres.length) {
                body.appendChild(el("div", "sl-preview-genres", genres.slice(0, 3).join(" • ")));
            }
        }

        preview.appendChild(media);
        preview.appendChild(body);
        preview.addEventListener("mouseleave", (event) => {
            closePreview(false);
            // The preview covers part of the neighbouring cards, so the pointer may already be
            // on another card when it closes, and that card never gets its own mouseenter.
            const under = document.elementFromPoint(event.clientX, event.clientY);
            const next = under && under.closest(".sl-card");
            if (next && next !== card && cardData.has(next)) {
                schedulePreview(next, cardData.get(next));
            }
        });
        document.body.appendChild(preview);

        window.requestAnimationFrame(() => preview.classList.add("is-open"));
        state.preview = preview;
        pauseHero("preview");
    }

    function closePreview(immediate) {
        cancelPreviewTimer();
        if (openRatePop && state.preview && state.preview.contains(openRatePop)) {
            closeRatePop();
        }
        const preview = state.preview;
        if (!preview) {
            return;
        }
        state.preview = null;
        resumeHero("preview");
        if (immediate || REDUCED_MOTION_QUERY.matches) {
            preview.remove();
            return;
        }
        preview.classList.remove("is-open");
        window.setTimeout(() => preview.remove(), 200);
    }

    function buildMetaLine(item) {
        const meta = el("div", "sl-meta");
        if (item.CommunityRating) {
            const rating = el("span", "sl-meta-rating");
            rating.appendChild(icon("star"));
            rating.appendChild(document.createTextNode(item.CommunityRating.toFixed(1)));
            meta.appendChild(rating);
        }
        if (item.CriticRating) {
            // Drawn in CSS rather than an emoji: not every TV/phone web view has an emoji font.
            const critic = el("span", "sl-meta-critic");
            const tomato = el("span", "sl-tomato");
            tomato.setAttribute("aria-hidden", "true");
            critic.appendChild(tomato);
            critic.appendChild(document.createTextNode(`${Math.round(item.CriticRating)}%`));
            critic.title = "Rotten Tomatoes";
            meta.appendChild(critic);
        }
        if (item.OfficialRating) {
            meta.appendChild(el("span", "sl-badge", item.OfficialRating));
        }
        for (const part of metaParts(item)) {
            if (item.Type === "Episode" && part.startsWith("S")) {
                continue;
            }
            meta.appendChild(el("span", null, part));
        }
        return meta;
    }

    // ---------------------------------------------------------------------------------------
    // Rows
    // ---------------------------------------------------------------------------------------

    function buildSkeletonRow() {
        const section = el("section", "sl-row sl-row--loading");
        section.setAttribute("aria-hidden", "true");
        section.appendChild(el("div", "sl-skeleton sl-skeleton--title"));
        const track = el("div", "sl-row-track");
        for (let i = 0; i < 6; i++) {
            track.appendChild(el("div", "sl-skeleton sl-skeleton--card"));
        }
        section.appendChild(track);
        return section;
    }

    function buildRow(row) {
        const section = el("section", `sl-row sl-row--${row.layout}`);
        section.dataset.row = row.key;
        const headingId = `sl-row-${row.key}`;
        section.setAttribute("aria-labelledby", headingId);

        section.slRow = row;
        const heading = el("h2", "sl-row-title");
        heading.id = headingId;
        if (row.seeAll) {
            const link = el("a", "sl-row-link");
            link.href = `#${row.seeAll}`;
            link.appendChild(el("span", null, row.title));
            const explore = el("span", "sl-row-explore", "Explore all");
            explore.appendChild(icon("chevron_right"));
            link.appendChild(explore);
            link.addEventListener("click", (event) => {
                event.preventDefault();
                navigate(row.seeAll);
            });
            heading.appendChild(link);
        } else {
            heading.textContent = row.title;
        }
        if (row.customizeBar) {
            // A thin bar above the genre rows: a clear divide, saying what they are and how to
            // change them.
            const bar = el("div", "sl-genre-bar");
            const text = el("div", "sl-genre-bar-text");
            text.appendChild(el("span", "sl-genre-bar-title", "Your genres"));
            text.appendChild(el("span", "sl-genre-bar-sub", "In the order of what you watch. Pin the ones you love to the top, hide the ones you don't."));
            bar.appendChild(text);
            const button = el("button", "sl-genre-bar-btn");
            button.type = "button";
            button.appendChild(icon("tune"));
            button.appendChild(el("span", null, "Customize"));
            button.setAttribute("aria-label", "Customize your genre rows");
            button.addEventListener("click", row.customizeBar);
            bar.appendChild(button);
            section.appendChild(bar);
        }
        section.appendChild(heading);

        const viewport = el("div", "sl-row-viewport");
        // "scrollX" isn't styling: Jellyfin's swipe-between-tabs handler skips touches that
        // start inside an element with this class, which every native row carries too.
        // Without it, swiping this row can switch the home screen to the Favorites tab.
        const track = el("div", "sl-row-track scrollX");
        row.items.forEach((item, index) => track.appendChild(row.cardBuilder ? row.cardBuilder(item) : buildCard(item, row, index)));
        if (row.seeAll) {
            track.appendChild(buildMoreCard(row));
        }

        const prev = el("button", "sl-row-arrow sl-row-arrow--prev");
        prev.type = "button";
        prev.setAttribute("aria-label", `Scroll ${row.title} left`);
        prev.appendChild(icon("chevron_left"));
        const next = el("button", "sl-row-arrow sl-row-arrow--next");
        next.type = "button";
        next.setAttribute("aria-label", `Scroll ${row.title} right`);
        next.appendChild(icon("chevron_right"));

        const page = (direction) => {
            closePreview(true);
            track.scrollBy({ left: direction * track.clientWidth * 0.85, behavior: REDUCED_MOTION_QUERY.matches ? "auto" : "smooth" });
        };
        prev.addEventListener("click", () => page(-1));
        next.addEventListener("click", () => page(1));

        // The arrows only exist for mouse users (CSS hides them on touch), so updating them on
        // scroll can't interfere with touch momentum scrolling.
        let pending = false;
        const updateArrows = () => {
            pending = false;
            prev.hidden = track.scrollLeft <= 2;
            next.hidden = track.scrollLeft + track.clientWidth >= track.scrollWidth - 2;
        };
        track.addEventListener("scroll", () => {
            if (HOVER_QUERY.matches && !pending) {
                pending = true;
                window.requestAnimationFrame(updateArrows);
            }
        }, { passive: true });
        window.requestAnimationFrame(updateArrows);
        section.updateArrows = updateArrows;

        viewport.appendChild(prev);
        viewport.appendChild(track);
        viewport.appendChild(next);
        section.appendChild(viewport);
        return section;
    }

    function fillSlot(holder, rows) {
        const visible = rows.filter((row) => row.items && row.items.length);
        if (!visible.length) {
            holder.replaceChildren();
            holder.hidden = true;
            return;
        }
        holder.hidden = false;
        holder.replaceChildren(...visible.map(buildRow));
    }

    function fillFromSlot(holder, slot) {
        loadSlot(slot)
            .then((rows) => {
                if (holder.isConnected) {
                    fillSlot(holder, rows);
                }
            })
            .catch((error) => {
                console.warn(`[Streamline] Row "${slot.key}" failed to load.`, error);
                holder.replaceChildren();
                holder.hidden = true;
            });
    }

    function renderSlots(container, slots) {
        for (const slot of slots) {
            const holder = el("div", "sl-slot");
            holder.slSlot = slot;
            holder.appendChild(buildSkeletonRow());
            container.appendChild(holder);

            if (slot.expand) {
                // A slot that turns into several rows once we know what they are (the genre list).
                slot.expand()
                    .then((children) => {
                        if (!holder.isConnected) {
                            return;
                        }
                        holder.replaceChildren();
                        holder.classList.add("sl-slot-group");
                        renderSlots(holder, children);
                    })
                    .catch(() => holder.remove());
            } else if (slot.lazy && "IntersectionObserver" in window) {
                // Long lists of rows only load as they come near the screen.
                const observer = new IntersectionObserver((entries) => {
                    if (entries.some((entry) => entry.isIntersecting)) {
                        observer.disconnect();
                        fillFromSlot(holder, slot);
                    }
                }, { rootMargin: "700px 0px" });
                observer.observe(holder);
            } else {
                fillFromSlot(holder, slot);
            }
        }
    }

    function refreshVolatileRows(root) {
        invalidate("watchlist");
        // Continue Watching and Watch List change from other pages (finishing an episode, adding
        // something from a details page), so re-fetch them every time their page is shown.
        root.querySelectorAll(".sl-slot").forEach((holder) => {
            const slot = holder.slSlot;
            if (slot && slot.ttl === 0) {
                fillFromSlot(holder, slot);
            }
        });
    }

    // ---------------------------------------------------------------------------------------
    // Hero banner
    // ---------------------------------------------------------------------------------------

    async function loadHeroItems(scope) {
        const config = state.config;
        const count = config.HeroItemCount || 6;
        const cutoffMs = Date.now() - (config.HeroMaxAgeDays || 90) * 86400000;
        const cutoff = new Date(cutoffMs).toISOString();
        // Jellyfin only fills in a series' PlayedPercentage when its episode count is asked for too.
        const fields = `${ITEM_FIELDS},RecursiveItemCount`;
        const hasBackdrop = (item) => item.BackdropImageTags && item.BackdropImageTags.length;
        const unstarted = (item) => !(item.UserData && (item.UserData.Played || item.UserData.PlayedPercentage > 0 || item.UserData.PlaybackPositionTicks > 0));
        const released = (item) => Date.parse(item.PremiereDate || "") || 0;

        const types = scope.types.split(",");

        // Movies released within the window that this user hasn't seen.
        const moviesPromise = !types.includes("Movie") ? Promise.resolve([]) : queryItems({
            ParentId: scope.parentId || undefined,
            IncludeItemTypes: "Movie",
            MinPremiereDate: cutoff,
            IsPlayed: false,
            ImageTypes: "Backdrop",
            SortBy: "PremiereDate",
            SortOrder: "Descending",
            Limit: count * 2,
            Fields: fields
        });

        // Shows with episodes that aired within the window: brand-new shows, new seasons, or new
        // episodes of a running season. Shows the user already watches count too (that's the
        // point of "new episodes"), as long as there's something left unwatched.
        const episodes = !types.includes("Series") ? null : await getJson("Items", {
            UserId: userId(),
            Recursive: true,
            ParentId: scope.parentId || undefined,
            IncludeItemTypes: "Episode",
            MinPremiereDate: cutoff,
            SortBy: "PremiereDate",
            SortOrder: "Descending",
            Limit: 300,
            EnableImages: false,
            EnableUserData: false
        }).catch(() => null);

        const shows = new Map();
        for (const episode of (episodes && episodes.Items) || []) {
            if (!episode.SeriesId || released(episode) > Date.now()) {
                continue;
            }
            const show = shows.get(episode.SeriesId) || { latest: 0, newSeason: false, firstSeason: false };
            show.latest = Math.max(show.latest, released(episode));
            if (episode.IndexNumber === 1) {
                if ((episode.ParentIndexNumber || 1) > 1) {
                    show.newSeason = true;
                } else {
                    show.firstSeason = true;
                }
            }
            shows.set(episode.SeriesId, show);
        }

        const [movies, series] = await Promise.all([
            moviesPromise,
            itemsByIds(Array.from(shows.keys()).slice(0, count * 2), fields)
        ]);

        const candidates = [];
        for (const movie of movies) {
            if (hasBackdrop(movie) && unstarted(movie)) {
                candidates.push({ item: movie, date: released(movie) });
            }
        }
        for (const show of series) {
            const info = shows.get(show.Id);
            if (!hasBackdrop(show) || (show.UserData && show.UserData.UnplayedItemCount === 0)) {
                continue;
            }
            candidates.push({ item: show, date: info.latest });
        }

        // Newest first, alternating movies and shows when there are both, so home doesn't end up
        // all shows just because episodes come out more often than movies.
        const newest = (type) => candidates.filter((c) => c.item.Type === type).sort((a, b) => b.date - a.date);
        const lists = [newest("Movie"), newest("Series")].filter((list) => list.length);
        const result = [];
        for (let i = 0; result.length < count && lists.some((list) => i < list.length); i++) {
            lists.forEach((list) => {
                if (i < list.length && result.length < count) {
                    result.push(list[i].item);
                }
            });
        }

        // A quiet stretch with hardly anything new: rather than a lonely banner, top up with what
        // was most recently added to the server.
        const minimum = Math.min(3, count);
        if (result.length < minimum) {
            const seen = new Set(result.map((item) => item.Id));
            const added = await scopeQuery(scope, { SortBy: "DateCreated", SortOrder: "Descending", IsPlayed: false, ImageTypes: "Backdrop", Limit: count * 2, Fields: fields });
            for (const item of added) {
                if (result.length >= minimum) {
                    break;
                }
                if (!seen.has(item.Id) && hasBackdrop(item) && unstarted(item)) {
                    result.push(item);
                }
            }
        }
        return result;
    }

    function buildHeroSlide(item, index) {
        const slide = el("div", "sl-hero-slide");
        slide.dataset.index = String(index);
        slide.setAttribute("role", "group");
        slide.setAttribute("aria-roledescription", "slide");
        slide.setAttribute("aria-label", item.Name);

        const backdropWidth = Math.min(2560, Math.round(window.innerWidth * (window.devicePixelRatio || 1)));
        const backdrop = el("img", "sl-hero-backdrop");
        backdrop.alt = "";
        backdrop.decoding = "async";
        backdrop.dataset.src = backdropUrl(item, backdropWidth);
        slide.appendChild(backdrop);

        const content = el("div", "sl-hero-content");
        const badge = badgeFor(item);
        if (badge) {
            content.appendChild(el("span", "sl-hero-badge", badge));
        }
        const logo = logoUrl(item, 640);
        if (logo) {
            const logoImg = el("img", "sl-hero-logo");
            logoImg.alt = item.Name;
            logoImg.decoding = "async";
            logoImg.dataset.src = logo;
            content.appendChild(logoImg);
        } else {
            content.appendChild(el("h2", "sl-hero-title", item.Name));
        }

        const meta = buildMetaLine(item);
        meta.classList.add("sl-meta--hero");
        if (item.Genres && item.Genres.length) {
            const genres = shownGenres(item);
            if (genres.length) {
                meta.appendChild(el("span", "sl-meta-genres", genres.slice(0, 3).join(" • ")));
            }
        }
        content.appendChild(meta);

        if (item.Overview) {
            content.appendChild(el("p", "sl-hero-overview", item.Overview));
        }

        const actions = el("div", "sl-hero-actions");
        const playButton = el("button", "sl-btn sl-btn--play");
        playButton.type = "button";
        playButton.appendChild(icon("play_arrow"));
        const resumable = item.UserData && item.UserData.PlaybackPositionTicks > 0 && !item.UserData.Played;
        playButton.appendChild(el("span", null, resumable ? "Resume" : "Play"));
        playButton.addEventListener("click", () => play(item));

        const infoButton = el("button", "sl-btn sl-btn--info");
        infoButton.type = "button";
        infoButton.appendChild(icon("info_outline"));
        infoButton.appendChild(el("span", null, "More info"));
        infoButton.addEventListener("click", () => showDetails(item));

        actions.appendChild(playButton);
        actions.appendChild(infoButton);
        actions.appendChild(listButton(item.Id, "sl-round sl-round--hero"));
        actions.appendChild(favoriteButton(item, "sl-round sl-round--hero"));
        actions.appendChild(rateButton(item, "sl-round sl-round--hero"));
        content.appendChild(actions);

        slide.appendChild(content);
        return slide;
    }

    function loadSlideImages(slide) {
        if (!slide) {
            return;
        }
        slide.querySelectorAll("img[data-src]").forEach((img) => {
            img.addEventListener("load", () => img.classList.add("sl-loaded"), { once: true });
            img.src = img.dataset.src;
            img.removeAttribute("data-src");
        });
    }

    function buildHero(items, root) {
        const hero = el("section", "sl-hero");
        hero.setAttribute("aria-roledescription", "carousel");
        hero.setAttribute("aria-label", "Featured");

        const slides = el("div", "sl-hero-slides");
        slides.setAttribute("aria-live", "off");
        items.forEach((item, index) => slides.appendChild(buildHeroSlide(item, index)));
        hero.appendChild(slides);

        const dots = el("div", "sl-hero-dots");
        dots.setAttribute("role", "tablist");
        dots.setAttribute("aria-label", "Choose featured title");
        items.forEach((item, index) => {
            const dot = el("button", "sl-hero-dot");
            dot.type = "button";
            dot.setAttribute("role", "tab");
            dot.setAttribute("aria-label", item.Name);
            dot.appendChild(el("span", "sl-hero-dot-fill"));
            dot.addEventListener("click", () => {
                showSlide(index, heroState);
                restartHeroTimer(heroState);
            });
            dots.appendChild(dot);
        });
        if (items.length > 1) {
            hero.appendChild(dots);
        }

        if (items.length > 1) {
            const prev = el("button", "sl-hero-arrow sl-hero-arrow--prev");
            prev.type = "button";
            prev.setAttribute("aria-label", "Previous title");
            prev.appendChild(icon("chevron_left"));
            prev.addEventListener("click", () => step(-1));
            const next = el("button", "sl-hero-arrow sl-hero-arrow--next");
            next.type = "button";
            next.setAttribute("aria-label", "Next title");
            next.appendChild(icon("chevron_right"));
            next.addEventListener("click", () => step(1));
            hero.appendChild(prev);
            hero.appendChild(next);
        }

        hero.addEventListener("mouseenter", () => pauseHero("hover", heroState));
        hero.addEventListener("mouseleave", () => resumeHero("hover", heroState));
        hero.addEventListener("focusin", () => pauseHero("focus", heroState));
        hero.addEventListener("focusout", () => resumeHero("focus", heroState));

        // Touch swipe between slides. Horizontal only; vertical drags still scroll the page.
        let touchStart = null;
        hero.addEventListener("touchstart", (event) => {
            const touch = event.touches[0];
            touchStart = { x: touch.clientX, y: touch.clientY };
        }, { passive: true });
        hero.addEventListener("touchend", (event) => {
            if (!touchStart) {
                return;
            }
            const touch = event.changedTouches[0];
            const dx = touch.clientX - touchStart.x;
            const dy = touch.clientY - touchStart.y;
            touchStart = null;
            if (Math.abs(dx) > 50 && Math.abs(dx) > Math.abs(dy) * 1.5) {
                step(dx < 0 ? 1 : -1);
            }
        }, { passive: true });

        // Each page (home, Movies, Shows) keeps its own banner; only the one on the page being
        // shown is "the" banner that page-level events (hidden tab, preview open) act on.
        const heroState = { element: hero, items, index: -1, timer: 0, pausedBy: new Set() };
        root.slHero = heroState;
        if (root === state.root && state.view) {
            state.hero = heroState;
        } else {
            heroState.pausedBy.add("away");
        }

        function step(direction) {
            showSlide((heroState.index + direction + items.length) % items.length, heroState);
            restartHeroTimer(heroState);
        }

        // Pause while the banner is scrolled out of view: no point animating what nobody sees.
        if ("IntersectionObserver" in window) {
            new IntersectionObserver((entries) => {
                entries.forEach((entry) => (entry.isIntersecting ? resumeHero("offscreen", heroState) : pauseHero("offscreen", heroState)));
            }, { threshold: 0.25 }).observe(hero);
        }

        showSlide(0, heroState);
        return hero;
    }

    function showSlide(index, hero = state.hero) {
        if (!hero) {
            return;
        }
        hero.index = index;
        const slides = hero.element.querySelectorAll(".sl-hero-slide");
        slides.forEach((slide, i) => {
            const active = i === index;
            slide.classList.toggle("is-active", active);
            slide.setAttribute("aria-hidden", String(!active));
            slide.inert = !active;
        });
        hero.element.querySelectorAll(".sl-hero-dot").forEach((dot, i) => {
            dot.classList.toggle("is-active", i === index);
            dot.setAttribute("aria-selected", String(i === index));
        });
        loadSlideImages(slides[index]);
        // Preload the next one so the crossfade never shows a blank frame.
        loadSlideImages(slides[(index + 1) % slides.length]);
    }

    function heroInterval() {
        return ((state.config && state.config.HeroIntervalSeconds) || 9) * 1000;
    }

    function restartHeroTimer(hero = state.hero) {
        if (!hero) {
            return;
        }
        window.clearInterval(hero.timer);
        hero.timer = 0;
        hero.element.style.setProperty("--sl-hero-interval", `${heroInterval()}ms`);
        // Restart the dot's fill animation from zero.
        hero.element.classList.remove("is-running");
        void hero.element.offsetWidth;
        if (hero.items.length < 2 || hero.pausedBy.size || REDUCED_MOTION_QUERY.matches) {
            hero.element.classList.toggle("is-paused", hero.pausedBy.size > 0);
            return;
        }
        hero.element.classList.remove("is-paused");
        hero.element.classList.add("is-running");
        hero.timer = window.setInterval(() => {
            showSlide((hero.index + 1) % hero.items.length, hero);
            hero.element.classList.remove("is-running");
            void hero.element.offsetWidth;
            hero.element.classList.add("is-running");
        }, heroInterval());
    }

    function pauseHero(reason, hero = state.hero) {
        if (!hero || hero.pausedBy.has(reason)) {
            return;
        }
        hero.pausedBy.add(reason);
        window.clearInterval(hero.timer);
        hero.timer = 0;
        hero.element.classList.add("is-paused");
    }

    function resumeHero(reason, hero = state.hero) {
        if (!hero || !hero.pausedBy.delete(reason) || hero.pausedBy.size) {
            return;
        }
        restartHeroTimer(hero);
    }

    // ---------------------------------------------------------------------------------------
    // Header: transparent over the hero, library links, solid once scrolled
    // ---------------------------------------------------------------------------------------

    function loadViews() {
        return cached("views", STABLE_ROW_TTL_MS, async () => {
            const result = await getJson("UserViews", { UserId: userId() });
            return (result && result.Items) || [];
        });
    }

    // The nav bar stays in the header on every page, so Home, the libraries and Watch List are
    // always one click away.
    async function ensureNav() {
        const headerLeft = document.querySelector(".skinHeader .headerLeft");
        if (!headerLeft || headerLeft.querySelector(".sl-nav")) {
            return;
        }

        // Our own copy of the logo, so the header reads [menu] [logo] [links] on every page:
        // Jellyfin's title spot shows the logo on home but the page name elsewhere. The theme's
        // own logo class supplies the image, so it follows whatever theme is active.
        const logo = el("button", "sl-logo pageTitleWithLogo pageTitleWithDefaultLogo");
        logo.type = "button";
        logo.setAttribute("aria-label", "Home");
        logo.addEventListener("click", () => (visibleHomeTab() ? window.scrollTo({ top: 0, behavior: "smooth" }) : navigate("/home")));
        headerLeft.appendChild(logo);

        const nav = el("nav", "sl-nav");
        nav.setAttribute("aria-label", "Main");
        headerLeft.appendChild(nav);

        const views = await loadViews().catch(() => []);

        const link = (key, text, onClick) => {
            const button = el("button", "sl-nav-link", text);
            button.type = "button";
            button.dataset.nav = key;
            button.addEventListener("click", onClick);
            return button;
        };

        nav.replaceChildren(
            link("home", "Home", () => {
                if (visibleHomeTab()) {
                    window.scrollTo({ top: 0, behavior: "smooth" });
                } else {
                    navigate("/home");
                }
            }),
            // The libraries, but not Jellyfin's own Collections (our Collections replaces it),
            // playlists or Live TV.
            ...views.filter((view) => !["boxsets", "playlists", "livetv"].includes(view.CollectionType)).slice(0, 5)
                .map((view) => link(view.Id, view.Name, () => showDetails(view))),
            link("mylist", "Watch List", () => navigate(browseHref(HOME_SCOPE, { sl: "mylist" }))),
            link("favorites", "Favorites", () => navigate(browseHref(HOME_SCOPE, { sl: "favorites", IsFavorite: "true" }))),
            link("collections", "Collections", () => navigate(collectionsHref()))
        );
        updateNav();
    }

    // Phones: the top links don't fit, so the same places sit in a tab bar at the bottom.
    async function ensureTabBar() {
        if (document.querySelector(".sl-tabbar")) {
            return;
        }
        const bar = el("nav", "sl-tabbar");
        bar.setAttribute("aria-label", "Main");
        document.body.appendChild(bar);

        const views = await loadViews().catch(() => []);
        const movies = views.find((view) => view.CollectionType === "movies");
        const shows = views.find((view) => view.CollectionType === "tvshows");
        const tab = (key, label, iconName, go) => {
            const button = el("button", "sl-tab");
            button.type = "button";
            button.dataset.nav = key;
            button.appendChild(icon(iconName));
            button.appendChild(el("span", "sl-tab-label", label));
            button.addEventListener("click", go);
            return button;
        };
        bar.replaceChildren(...[
            tab("home", "Home", "home", () => (visibleHomeTab() ? window.scrollTo({ top: 0, behavior: "smooth" }) : navigate("/home"))),
            movies && tab(movies.Id, "Movies", "movie", () => showDetails(movies)),
            shows && tab(shows.Id, "Shows", "tv", () => showDetails(shows)),
            tab("mylist", "Watch List", "bookmark_border", () => navigate(browseHref(HOME_SCOPE, { sl: "mylist" }))),
            tab("favorites", "Favorites", "favorite_border", () => navigate(browseHref(HOME_SCOPE, { sl: "favorites", IsFavorite: "true" }))),
            tab("collections", "Collections", "video_library", () => navigate(collectionsHref()))
        ].filter(Boolean));
        updateNav();
    }

    function hashParams() {
        return new URLSearchParams(window.location.hash.split("?")[1] || "");
    }

    function currentNavKey() {
        if (visibleHomeTab()) {
            return "home";
        }
        // Search opened from a library page carries that library's id, but isn't that page.
        if (window.location.hash.startsWith("#/search")) {
            return null;
        }
        const params = hashParams();
        if (window.location.hash.startsWith("#/list") && ["mylist", "favorites"].includes(params.get("sl")) && !params.get("parentId")) {
            return params.get("sl");
        }
        if (window.location.hash.startsWith("#/list") && ["collections", "collection"].includes(params.get("sl"))) {
            return "collections";
        }
        // Library pages carry the library's id in the route (topParentId, or parentId on lists).
        return params.get("topParentId") || params.get("parentId") || null;
    }

    function updateNav() {
        const key = currentNavKey();
        document.querySelectorAll(".sl-nav .sl-nav-link, .sl-tabbar .sl-tab").forEach((button) => {
            const active = button.dataset.nav === key;
            button.classList.toggle("is-active", active);
            if (active) {
                button.setAttribute("aria-current", "page");
            } else {
                button.removeAttribute("aria-current");
            }
        });
    }

    function removeNav() {
        document.querySelectorAll(".skinHeader .sl-nav, .skinHeader .sl-logo, .sl-back, .sl-tabbar").forEach((node) => node.remove());
        document.documentElement.classList.remove("sl-nav-on");
    }

    function updateScrolled() {
        document.documentElement.classList.toggle("sl-scrolled", window.scrollY > 40);
    }

    let scrollPending = false;
    window.addEventListener("scroll", () => {
        if (state.preview) {
            closePreview(false);
        }
        if (!scrollPending && state.view) {
            scrollPending = true;
            window.requestAnimationFrame(() => {
                scrollPending = false;
                updateScrolled();
            });
        }
    }, { passive: true });

    // ---------------------------------------------------------------------------------------
    // Page lifecycle
    // ---------------------------------------------------------------------------------------

    function ensureStyle() {
        if (document.getElementById("streamlineStyle")) {
            return;
        }
        const link = document.createElement("link");
        link.id = "streamlineStyle";
        link.rel = "stylesheet";
        link.href = SCRIPT_URL ? SCRIPT_URL.replace(/\/script(\?|$)/, "/style$1") : url("Streamline/style");
        document.head.appendChild(link);
    }

    function updatePull(container) {
        // The banner sits underneath the (now transparent) header: pull it up by however far
        // the page content starts from the top, which varies with the header (and tabs).
        if (!state.root) {
            return;
        }
        const top = container.getBoundingClientRect().top + window.scrollY;
        state.root.style.setProperty("--sl-pull", `${Math.max(0, Math.round(top))}px`);
    }

    function buildFooter() {
        return el("footer", "sl-footer");
    }

    // Big tiles at the bottom of a page, for when nothing above caught the eye.
    function buildTiles(scope) {
        const section = el("section", "sl-browse");
        section.setAttribute("aria-label", "Browse");

        loadViews()
            .then(async (views) => {
                const series = views.find((view) => view.CollectionType === "tvshows");
                const movies = views.find((view) => view.CollectionType === "movies");
                let targets;
                if (scope.kind === "home") {
                    // Two by two: the libraries, then the two ways to browse across them.
                    section.classList.add("sl-browse--quad");
                    targets = [
                        series && { label: "Shows", noun: "shows", sampleScope: { parentId: series.Id, types: "Series" }, go: () => showDetails(series) },
                        movies && { label: "Movies", noun: "movies", sampleScope: { parentId: movies.Id, types: "Movie" }, go: () => showDetails(movies) },
                        { label: "Genres", sub: "Every genre and sub-genre", sampleScope: HOME_SCOPE, go: () => navigate(browseHref(HOME_SCOPE, { sl: "genres" })) },
                        { label: "Collections", sub: "Franchises, themes, decades and directors", sampleScope: HOME_SCOPE, go: () => navigate(collectionsHref()) }
                    ];
                } else {
                    // On a library page: everything in it A-Z, and every genre.
                    targets = [
                        { label: `All ${scope.noun}`, noun: `${scope.noun.toLowerCase()} A\u2013Z`, sampleScope: scope, go: () => navigate(browseHref(scope, { sl: "all" })) },
                        { label: "Genres", sub: "Every genre and sub-genre", sampleScope: scope, go: () => navigate(browseHref(scope, { sl: "genres" })) }
                    ];
                }

                const tiles = await Promise.all(targets.filter(Boolean).map(async (target) => {
                    const sample = await getJson("Items", {
                        UserId: userId(),
                        ParentId: target.sampleScope.parentId || undefined,
                        Recursive: true,
                        IncludeItemTypes: target.sampleScope.types,
                        ImageTypes: "Backdrop",
                        SortBy: "Random",
                        Limit: 1,
                        EnableImageTypes: "Backdrop",
                        ImageTypeLimit: 1
                    }).catch(() => null);

                    const tile = el("button", "sl-browse-tile");
                    tile.type = "button";
                    const item = sample && sample.Items && sample.Items[0];
                    const src = item ? backdropUrl(item, 1280) : "";
                    if (src) {
                        tile.appendChild(lazyImage(src, "sl-browse-img"));
                    }
                    const text = el("span", "sl-browse-text");
                    text.appendChild(el("span", "sl-browse-label", target.label));
                    const total = sample && sample.TotalRecordCount;
                    text.appendChild(el("span", "sl-browse-sub", target.sub || (total ? `Browse all ${total} ${target.noun}` : `Browse all ${target.noun}`)));
                    tile.appendChild(text);
                    tile.appendChild(icon("arrow_forward"));
                    tile.setAttribute("aria-label", `Browse ${target.label}`);
                    tile.addEventListener("click", target.go);
                    return tile;
                }));

                if (tiles.length) {
                    section.replaceChildren(...tiles);
                } else {
                    section.remove();
                }
            })
            .catch(() => section.remove());

        return section;
    }

    function buildRowsPage(root, view) {
        const config = state.config;
        const heroHolder = el("div", "sl-hero-holder");
        root.appendChild(heroHolder);
        if (config.HeroEnabled) {
            heroHolder.appendChild(el("div", "sl-hero sl-hero--loading"));
            Promise.all([cached(`${view.scope.key}:hero`, STABLE_ROW_TTL_MS, () => loadHeroItems(view.scope)), loadFreshness().catch(() => null)])
                .then(([items]) => {
                    if (!heroHolder.isConnected) {
                        return;
                    }
                    if (!items.length) {
                        heroHolder.replaceChildren();
                        root.classList.add("sl-root--no-hero");
                        syncPageClasses();
                        return;
                    }
                    heroHolder.replaceChildren(buildHero(items, root));
                    restartHeroTimer(root.slHero);
                })
                .catch((error) => {
                    console.warn("[Streamline] Hero banner failed to load.", error);
                    heroHolder.replaceChildren();
                    root.classList.add("sl-root--no-hero");
                    syncPageClasses();
                });
        } else {
            root.classList.add("sl-root--no-hero");
        }

        const rows = el("div", "sl-rows");
        root.appendChild(rows);
        renderSlots(rows, slotsFor(view.scope));
        root.appendChild(buildTiles(view.scope));
        root.appendChild(buildFooter());
    }

    const BROWSE_BATCH = 40;

    function buildBrowseHead(root, title, subtitleText) {
        const head = el("header", "sl-browse-head");
        head.appendChild(el("h1", "sl-browse-title", title));
        const subtitle = el("p", "sl-browse-subtitle", subtitleText);
        head.appendChild(subtitle);
        root.appendChild(head);
        return { head, subtitle };
    }

    function buildBrowsePage(root, view) {
        root.classList.add("sl-root--no-hero", "sl-root--browse");
        if (view.params.get("sl") === "genres") {
            buildGenresPage(root, view);
            return;
        }
        if (view.params.get("sl") === "collections") {
            buildCollectionsPage(root);
            return;
        }
        const source = browseSource(view.params);
        if (!source) {
            return;
        }

        const { head, subtitle } = buildBrowseHead(root, source.title, source.subtitle || source.scope.noun);
        if (source.collectionId) {
            loadCollections().then((collections) => {
                const collection = collections.find((entry) => entry.Id === source.collectionId);
                if (collection) {
                    head.querySelector(".sl-browse-title").textContent = collection.Name;
                    source.subtitle = collection.Kind === "director" ? "Director"
                        : collection.Movies && collection.Shows ? "Movies & Shows" : collection.Shows ? "Shows" : "Movies";
                    if (!/title/.test(subtitle.textContent)) {
                        subtitle.textContent = source.subtitle;
                    }
                }
            });
        }

        const controls = el("div", "sl-sort");
        const chips = el("div", "sl-sort-chips");
        chips.setAttribute("role", "radiogroup");
        chips.setAttribute("aria-label", "Sort by");
        const direction = el("button", "sl-sort-direction");
        direction.type = "button";
        controls.appendChild(chips);
        controls.appendChild(direction);
        head.appendChild(controls);

        const grid = el("div", "sl-grid");
        const sentinel = el("div", "sl-grid-sentinel");
        const empty = el("p", "sl-empty", source.empty || "Nothing here yet.");
        empty.hidden = true;
        root.appendChild(grid);
        root.appendChild(sentinel);
        root.appendChild(empty);
        root.appendChild(buildFooter());

        const pseudoRow = { key: "browse", title: source.title, layout: "landscape" };
        const sorts = source.sorts.map((key) => BROWSE_SORTS.find((sort) => sort.key === key));
        let current = sorts.find((sort) => sort.key === source.defaultSort) || sorts[0];
        let reversed = false;
        let run = 0;
        let observer = null;
        let whole = null;

        const showCount = (total) => {
            subtitle.textContent = [source.subtitle || source.scope.noun, `${total} ${total === 1 ? "title" : "titles"}`].join(" · ");
        };

        const start = () => {
            const token = ++run;
            const desc = current.desc !== reversed;
            grid.replaceChildren(...Array.from({ length: 12 }, () => el("div", "sl-skeleton sl-skeleton--grid")));
            empty.hidden = true;
            if (observer) {
                observer.disconnect();
            }

            chips.querySelectorAll(".sl-sort-chip").forEach((chip) => {
                const on = chip.dataset.sort === current.key;
                chip.classList.toggle("is-on", on);
                chip.setAttribute("aria-checked", String(on));
            });
            direction.replaceChildren(icon("swap_vert"), el("span", null, current.labels[reversed ? 1 : 0]));
            direction.setAttribute("aria-label", `Order: ${current.labels[reversed ? 1 : 0]}. Activate to reverse.`);

            let sorted = null;
            let offset = 0;
            let total = Infinity;
            let loading = false;

            const more = async () => {
                if (loading || offset >= total || token !== run) {
                    return;
                }
                loading = true;
                try {
                    await loadFreshness().catch(() => null);
                    let batch;
                    if (source.all) {
                        whole = whole || await source.all();
                        sorted = sorted || sortLocal(whole, current, desc);
                        total = sorted.length;
                        batch = sorted.slice(offset, offset + BROWSE_BATCH);
                    } else {
                        const result = await source.page(offset, BROWSE_BATCH, current, desc);
                        total = result.total;
                        batch = result.items;
                    }
                    if (token !== run) {
                        return;
                    }
                    if (offset === 0) {
                        grid.replaceChildren();
                        showCount(total);
                    }
                    batch.forEach((item, index) => grid.appendChild(buildCard(item, pseudoRow, offset + index)));
                    offset += batch.length;
                    if (!batch.length) {
                        total = offset;
                    }
                    empty.hidden = total > 0;
                } catch (error) {
                    console.warn("[Streamline] Could not load this page.", error);
                    if (offset === 0) {
                        grid.replaceChildren();
                        empty.hidden = false;
                    }
                    total = offset;
                } finally {
                    loading = false;
                }
                // Keep going while the end of the grid is still on screen.
                if (token === run && offset < total && sentinel.getBoundingClientRect().top < window.innerHeight + 800) {
                    more();
                }
            };

            if ("IntersectionObserver" in window) {
                observer = new IntersectionObserver((entries) => {
                    if (entries.some((entry) => entry.isIntersecting)) {
                        more();
                    }
                }, { rootMargin: "800px 0px" });
                observer.observe(sentinel);
            }
            more();
        };

        for (const sort of sorts) {
            const chip = el("button", "sl-sort-chip", sort.label);
            chip.type = "button";
            chip.dataset.sort = sort.key;
            chip.setAttribute("role", "radio");
            chip.addEventListener("click", () => {
                if (sort !== current) {
                    current = sort;
                    reversed = false;
                    start();
                }
            });
            chips.appendChild(chip);
        }
        direction.addEventListener("click", () => {
            reversed = !reversed;
            start();
        });

        start();
    }

    // Customize: each person's own genre rows. Pin a genre (always at the top, in the pinned
    // order), hide one (never a row), and whether the rest follows what they watch.
    async function openGenreCustomizer(scope) {
        if (document.querySelector(".sl-dialog")) {
            return;
        }
        const [prefs, candidates] = await Promise.all([loadGenrePrefs(), genreCandidates(scope)]);
        const lower = (name) => name.toLowerCase();
        let pinned = (prefs.Pinned || []).slice();
        let hidden = (prefs.Hidden || []).slice();
        let auto = prefs.Auto !== false;

        const overlay = el("div", "sl-dialog");
        const panel = el("div", "sl-dialog-panel");
        panel.setAttribute("role", "dialog");
        panel.setAttribute("aria-modal", "true");
        panel.setAttribute("aria-label", "Your genre rows");
        overlay.appendChild(panel);
        panel.appendChild(el("h2", "sl-dialog-title", "Your genre rows"));
        panel.appendChild(el("p", "sl-dialog-sub", "Only for you. Pinned genres come first, hidden ones never show as a row."));

        const autoLabel = el("label", "sl-dialog-auto");
        const autoBox = el("input");
        autoBox.type = "checkbox";
        autoBox.checked = auto;
        autoBox.addEventListener("change", () => {
            auto = autoBox.checked;
        });
        autoLabel.appendChild(autoBox);
        autoLabel.appendChild(el("span", null, "Order the rest by what I watch"));
        panel.appendChild(autoLabel);

        const list = el("div", "sl-dialog-list");
        panel.appendChild(list);

        const toggle = (array, name) => (array.some((other) => lower(other) === lower(name)) ? array.filter((other) => lower(other) !== lower(name)) : array.concat([name]));
        const button = (iconName, label, on, onClick) => {
            const b = el("button", `sl-dialog-btn${on ? " is-on" : ""}`);
            b.type = "button";
            b.setAttribute("aria-label", label);
            b.setAttribute("aria-pressed", String(!!on));
            b.title = label;
            b.appendChild(icon(iconName));
            b.addEventListener("click", onClick);
            return b;
        };
        const render = () => {
            const isPinned = (name) => pinned.some((other) => lower(other) === lower(name));
            const isHidden = (name) => hidden.some((other) => lower(other) === lower(name));
            const others = candidates.filter((name) => !isPinned(name)).sort((x, y) => genreLabel(x).localeCompare(genreLabel(y)));
            const rows = pinned.filter((name) => candidates.some((other) => lower(other) === lower(name))).concat(others);
            list.replaceChildren(...rows.map((name) => {
                const line = el("div", `sl-dialog-row${isHidden(name) ? " is-hidden" : ""}${isPinned(name) ? " is-pinned" : ""}`);
                line.appendChild(el("span", "sl-dialog-name", genreLabel(name)));
                const actions = el("span", "sl-dialog-actions");
                if (isPinned(name)) {
                    const at = pinned.findIndex((other) => lower(other) === lower(name));
                    const move = (by) => () => {
                        const next = pinned.slice();
                        const [moved] = next.splice(at, 1);
                        next.splice(Math.max(0, Math.min(next.length, at + by)), 0, moved);
                        pinned = next;
                        render();
                    };
                    actions.appendChild(button("arrow_upward", `Move ${genreLabel(name)} up`, false, move(-1)));
                    actions.appendChild(button("arrow_downward", `Move ${genreLabel(name)} down`, false, move(1)));
                }
                actions.appendChild(button("push_pin", `${isPinned(name) ? "Unpin" : "Pin"} ${genreLabel(name)}`, isPinned(name), () => {
                    pinned = toggle(pinned, name);
                    hidden = hidden.filter((other) => lower(other) !== lower(name));
                    render();
                }));
                actions.appendChild(button(isHidden(name) ? "visibility_off" : "visibility", `${isHidden(name) ? "Show" : "Hide"} ${genreLabel(name)}`, isHidden(name), () => {
                    hidden = toggle(hidden, name);
                    pinned = pinned.filter((other) => lower(other) !== lower(name));
                    render();
                }));
                line.appendChild(actions);
                return line;
            }));
        };
        render();

        const footer = el("div", "sl-dialog-footer");
        const close = () => {
            overlay.remove();
            document.removeEventListener("keydown", onKey);
        };
        const onKey = (event) => {
            if (event.key === "Escape") {
                close();
            }
        };
        const reset = el("button", "sl-dialog-text", "Reset");
        reset.type = "button";
        reset.addEventListener("click", () => {
            pinned = [];
            hidden = [];
            auto = true;
            autoBox.checked = true;
            render();
        });
        const cancel = el("button", "sl-dialog-text", "Cancel");
        cancel.type = "button";
        cancel.addEventListener("click", close);
        const save = el("button", "sl-dialog-save", "Save");
        save.type = "button";
        save.addEventListener("click", async () => {
            save.disabled = true;
            try {
                await ApiClient.ajax({
                    type: "POST",
                    url: ApiClient.getUrl("Streamline/GenrePrefs"),
                    data: JSON.stringify({ Auto: auto, Pinned: pinned, Hidden: hidden }),
                    contentType: "application/json"
                });
                close();
                invalidate("genre-prefs");
                invalidate("genres");
                rebuildCurrentPage();
            } catch (error) {
                console.warn("[Streamline] Saving genre rows failed.", error);
                save.disabled = false;
            }
        });
        footer.appendChild(reset);
        footer.appendChild(el("span", "sl-dialog-spacer"));
        footer.appendChild(cancel);
        footer.appendChild(save);
        panel.appendChild(footer);

        overlay.addEventListener("click", (event) => {
            if (event.target === overlay) {
                close();
            }
        });
        document.addEventListener("keydown", onKey);
        document.body.appendChild(overlay);
        save.focus({ preventScroll: true });
    }

    // Builds the page that's showing again (after changing genre rows).
    function rebuildCurrentPage() {
        if (state.root) {
            destroyRoot(state.root);
        }
        state.root = null;
        state.view = null;
        scheduleCheck();
    }

    // All Genres: poster-shaped tiles, each a little collage of what's in it.
    function buildGenresPage(root, view) {
        const scope = scopeFromParams(view.params);
        const { subtitle } = buildBrowseHead(root, "Genres", scope.noun);
        const body = el("div", "sl-genre-sections");
        body.appendChild(el("div", "sl-genre-grid sl-genre-grid--loading"));
        body.firstChild.append(...Array.from({ length: 12 }, () => el("div", "sl-skeleton sl-skeleton--tile")));
        root.appendChild(body);
        root.appendChild(buildFooter());

        cached(`${scope.key}:genre-tiles`, STABLE_ROW_TTL_MS, () => genreTiles(scope))
            .then((sections) => {
                if (!body.isConnected) {
                    return;
                }
                const count = sections.reduce((sum, section) => sum + section.tiles.length, 0);
                subtitle.textContent = `${scope.noun} · ${count} ${count === 1 ? "genre" : "genres"}`;
                body.replaceChildren(...sections.map((section) => {
                    const block = el("section", "sl-genre-section");
                    block.setAttribute("aria-label", section.headline ? "Main genres" : "More genres");
                    const grid = el("div", section.headline ? "sl-genre-grid sl-genre-grid--headline" : "sl-genre-grid");
                    section.tiles.forEach((tile) => grid.appendChild(buildGenreTile(tile)));
                    block.appendChild(grid);
                    return block;
                }));
            })
            .catch((error) => {
                console.warn("[Streamline] Could not load the genres.", error);
                body.replaceChildren(el("p", "sl-empty", "Couldn't load the genres."));
            });
    }

    // The Collections page's sections, in their standard order. Featured, franchises and anime
    // get big tiles; the rest smaller.
    const COLLECTION_SECTIONS = [
        { kind: "featured", title: "Featured", headline: true },
        { kind: "universe", title: "Franchises", headline: true },
        { kind: "anime", title: "Anime", headline: true },
        { kind: "tv", title: "TV channels & kinds of TV", headline: false },
        { kind: "collection", title: "Movie series", headline: false },
        { kind: "decade", title: "Decades", headline: false },
        { kind: "director", title: "Directors", headline: false }
    ];

    function loadCollectionPrefs() {
        return cached("collection-prefs", STABLE_ROW_TTL_MS, async () => Object.assign({ Sections: [], HiddenSections: [], Pinned: [], Hidden: [] }, await getJson("Streamline/CollectionPrefs").catch(() => null)));
    }

    // The sections in this person's order (any they haven't placed keep their standard spot at
    // the end), and whether each is hidden.
    function orderedSections(prefs) {
        const known = (prefs.Sections || []).map((kind) => COLLECTION_SECTIONS.find((section) => section.kind === kind)).filter(Boolean);
        return known.concat(COLLECTION_SECTIONS.filter((section) => !known.includes(section)))
            .map((section) => Object.assign({}, section, { hidden: (prefs.HiddenSections || []).includes(section.kind) }));
    }

    // A person's collections: their pinned ones first (in their order), hidden ones and hidden
    // sections left out.
    function visibleCollections(collections, prefs) {
        const hiddenKinds = new Set(prefs.HiddenSections || []);
        const hidden = new Set(prefs.Hidden || []);
        const pinned = (prefs.Pinned || []).map((id) => collections.find((collection) => collection.Id === id)).filter(Boolean);
        return {
            pinned,
            rest: collections.filter((collection) => !hidden.has(collection.Id) && !hiddenKinds.has(collection.Kind) && !pinned.includes(collection))
        };
    }

    // Collections: the same poster tiles as Genres, in sections, the way each person set them up.
    function buildCollectionsPage(root) {
        const { head, subtitle } = buildBrowseHead(root, "Collections", "Themes, franchises, anime, TV channels, directors and movie series");
        const customize = el("button", "sl-genre-bar-btn sl-collections-customize");
        customize.type = "button";
        customize.appendChild(icon("tune"));
        customize.appendChild(el("span", null, "Customize"));
        customize.setAttribute("aria-label", "Customize your collections page");
        customize.addEventListener("click", () => openCollectionCustomizer());
        head.appendChild(customize);
        const body = el("div", "sl-genre-sections");
        body.appendChild(el("div", "sl-genre-grid sl-genre-grid--loading"));
        body.firstChild.append(...Array.from({ length: 12 }, () => el("div", "sl-skeleton sl-skeleton--tile")));
        root.appendChild(body);
        root.appendChild(buildFooter());

        Promise.all([loadCollections(), loadCollectionPrefs()])
            .then(async ([collections, prefs]) => {
                const { pinned, rest } = visibleCollections(collections, prefs);
                const tiles = await collectionTiles(pinned.concat(rest));
                return { tiles, pinnedCount: pinned.length, prefs };
            })
            .then(({ tiles, pinnedCount, prefs }) => {
                if (!body.isConnected) {
                    return;
                }
                if (!tiles.length) {
                    body.replaceChildren(el("p", "sl-empty", "No collections to show. A movie series shows up here once 3 of its movies are on the server, a franchise once it has a movie and a show (or a show and its spin-off). Hidden something by mistake? Customize puts it back."));
                    return;
                }
                subtitle.textContent = `${tiles.length} ${tiles.length === 1 ? "collection" : "collections"}`;
                const sections = [{ kind: "pinned", title: "Pinned", headline: true, tiles: tiles.slice(0, pinnedCount) }]
                    .concat(orderedSections(prefs).filter((section) => !section.hidden)
                        .map((section) => Object.assign(section, { tiles: tiles.slice(pinnedCount).filter((tile) => tile.kind === section.kind) })));
                body.replaceChildren(...sections.filter((section) => section.tiles.length).map((section) => {
                    const block = el("section", "sl-genre-section");
                    block.setAttribute("aria-label", section.title);
                    block.appendChild(el("h2", "sl-row-title sl-collection-heading", section.title));
                    const grid = el("div", section.headline ? "sl-genre-grid sl-genre-grid--headline" : "sl-genre-grid");
                    section.tiles.forEach((tile) => grid.appendChild(buildGenreTile(tile)));
                    block.appendChild(grid);
                    return block;
                }));
            })
            .catch((error) => {
                console.warn("[Streamline] Could not load the collections.", error);
                body.replaceChildren(el("p", "sl-empty", "Couldn't load the collections."));
            });
    }

    // Customize the Collections page, for this person only: move sections up or down or hide
    // them, pin collections to the top, hide collections they don't care about.
    async function openCollectionCustomizer() {
        if (document.querySelector(".sl-dialog")) {
            return;
        }
        const [collections, prefs] = await Promise.all([loadCollections(), loadCollectionPrefs()]);
        let sections = orderedSections(prefs);
        let pinned = (prefs.Pinned || []).filter((id) => collections.some((collection) => collection.Id === id));
        let hidden = (prefs.Hidden || []).slice();
        let filter = "";

        const overlay = el("div", "sl-dialog");
        const panel = el("div", "sl-dialog-panel");
        panel.setAttribute("role", "dialog");
        panel.setAttribute("aria-modal", "true");
        panel.setAttribute("aria-label", "Your collections page");
        overlay.appendChild(panel);
        panel.appendChild(el("h2", "sl-dialog-title", "Your collections page"));
        panel.appendChild(el("p", "sl-dialog-sub", "Only for you. Put the sections you care about first, hide the ones you don't, and pin your favourite collections to the top."));

        const button = (iconName, label, on, onClick) => {
            const b = el("button", `sl-dialog-btn${on ? " is-on" : ""}`);
            b.type = "button";
            b.setAttribute("aria-label", label);
            b.setAttribute("aria-pressed", String(!!on));
            b.title = label;
            b.appendChild(icon(iconName));
            b.addEventListener("click", onClick);
            return b;
        };

        panel.appendChild(el("h3", "sl-dialog-group", "Sections"));
        const sectionList = el("div", "sl-dialog-list sl-dialog-list--short");
        panel.appendChild(sectionList);
        const renderSections = () => {
            sectionList.replaceChildren(...sections.map((section, at) => {
                const line = el("div", `sl-dialog-row${section.hidden ? " is-hidden" : ""}`);
                line.appendChild(el("span", "sl-dialog-name", section.title));
                const actions = el("span", "sl-dialog-actions");
                const move = (by) => () => {
                    const next = sections.slice();
                    const [moved] = next.splice(at, 1);
                    next.splice(Math.max(0, Math.min(next.length, at + by)), 0, moved);
                    sections = next;
                    renderSections();
                    renderCollections();
                };
                actions.appendChild(button("arrow_upward", `Move ${section.title} up`, false, move(-1)));
                actions.appendChild(button("arrow_downward", `Move ${section.title} down`, false, move(1)));
                actions.appendChild(button(section.hidden ? "visibility_off" : "visibility", `${section.hidden ? "Show" : "Hide"} ${section.title}`, section.hidden, () => {
                    sections = sections.map((other) => (other === section ? Object.assign({}, other, { hidden: !other.hidden }) : other));
                    renderSections();
                }));
                line.appendChild(actions);
                return line;
            }));
        };

        panel.appendChild(el("h3", "sl-dialog-group", "Collections"));
        const search = el("input", "sl-dialog-filter");
        search.type = "search";
        search.placeholder = "Find a collection";
        search.setAttribute("aria-label", "Find a collection");
        search.addEventListener("input", () => {
            filter = searchWords(search.value);
            renderCollections();
        });
        panel.appendChild(search);
        const list = el("div", "sl-dialog-list");
        panel.appendChild(list);
        const sectionTitle = (kind) => (COLLECTION_SECTIONS.find((section) => section.kind === kind) || { title: "" }).title;
        const renderCollections = () => {
            const isPinned = (id) => pinned.includes(id);
            const isHidden = (id) => hidden.includes(id);
            const matching = collections.filter((collection) => !filter || searchWords(collection.Name).includes(filter));
            const rows = pinned.map((id) => matching.find((collection) => collection.Id === id)).filter(Boolean)
                .concat(sections.flatMap((section) => matching.filter((collection) => collection.Kind === section.kind && !isPinned(collection.Id))));
            list.replaceChildren(...rows.map((collection) => {
                const line = el("div", `sl-dialog-row${isHidden(collection.Id) ? " is-hidden" : ""}${isPinned(collection.Id) ? " is-pinned" : ""}`);
                const name = el("span", "sl-dialog-name", collection.Name);
                name.appendChild(el("span", "sl-dialog-kind", sectionTitle(collection.Kind)));
                line.appendChild(name);
                const actions = el("span", "sl-dialog-actions");
                if (isPinned(collection.Id)) {
                    const at = pinned.indexOf(collection.Id);
                    const move = (by) => () => {
                        const next = pinned.slice();
                        next.splice(at, 1);
                        next.splice(Math.max(0, Math.min(next.length, at + by)), 0, collection.Id);
                        pinned = next;
                        renderCollections();
                    };
                    actions.appendChild(button("arrow_upward", `Move ${collection.Name} up`, false, move(-1)));
                    actions.appendChild(button("arrow_downward", `Move ${collection.Name} down`, false, move(1)));
                }
                actions.appendChild(button("push_pin", `${isPinned(collection.Id) ? "Unpin" : "Pin"} ${collection.Name}`, isPinned(collection.Id), () => {
                    pinned = isPinned(collection.Id) ? pinned.filter((id) => id !== collection.Id) : pinned.concat([collection.Id]);
                    hidden = hidden.filter((id) => id !== collection.Id);
                    renderCollections();
                }));
                actions.appendChild(button(isHidden(collection.Id) ? "visibility_off" : "visibility", `${isHidden(collection.Id) ? "Show" : "Hide"} ${collection.Name}`, isHidden(collection.Id), () => {
                    hidden = isHidden(collection.Id) ? hidden.filter((id) => id !== collection.Id) : hidden.concat([collection.Id]);
                    pinned = pinned.filter((id) => id !== collection.Id);
                    renderCollections();
                }));
                line.appendChild(actions);
                return line;
            }));
            if (!rows.length) {
                list.appendChild(el("p", "sl-dialog-sub", "No collection by that name."));
            }
        };
        renderSections();
        renderCollections();

        const footer = el("div", "sl-dialog-footer");
        const close = () => {
            overlay.remove();
            document.removeEventListener("keydown", onKey);
        };
        const onKey = (event) => {
            if (event.key === "Escape") {
                close();
            }
        };
        const reset = el("button", "sl-dialog-text", "Reset");
        reset.type = "button";
        reset.addEventListener("click", () => {
            sections = orderedSections({});
            pinned = [];
            hidden = [];
            renderSections();
            renderCollections();
        });
        const cancel = el("button", "sl-dialog-text", "Cancel");
        cancel.type = "button";
        cancel.addEventListener("click", close);
        const save = el("button", "sl-dialog-save", "Save");
        save.type = "button";
        save.addEventListener("click", async () => {
            save.disabled = true;
            try {
                await ApiClient.ajax({
                    type: "POST",
                    url: ApiClient.getUrl("Streamline/CollectionPrefs"),
                    data: JSON.stringify({
                        Sections: sections.map((section) => section.kind),
                        HiddenSections: sections.filter((section) => section.hidden).map((section) => section.kind),
                        Pinned: pinned,
                        Hidden: hidden
                    }),
                    contentType: "application/json"
                });
                close();
                invalidate("collection-prefs");
                invalidate("collections");
                rebuildCurrentPage();
            } catch (error) {
                console.warn("[Streamline] Saving the collections page failed.", error);
                save.disabled = false;
            }
        });
        footer.appendChild(reset);
        footer.appendChild(el("span", "sl-dialog-spacer"));
        footer.appendChild(cancel);
        footer.appendChild(save);
        panel.appendChild(footer);

        overlay.addEventListener("click", (event) => {
            if (event.target === overlay) {
                close();
            }
        });
        document.addEventListener("keydown", onKey);
        document.body.appendChild(overlay);
        save.focus({ preventScroll: true });
    }

    function buildGenreTile(tile) {
        const button = el("a", "sl-genre-tile");
        button.href = `#${tile.href}`;
        button.setAttribute("aria-label", `${tile.name}, ${tile.total} titles`);
        const mosaic = el("div", `sl-genre-mosaic sl-genre-mosaic--${Math.min(tile.items.length, 4)}`);
        tile.items.forEach((item) => {
            const src = posterUrl(item, 300);
            if (src) {
                mosaic.appendChild(lazyImage(src, "sl-genre-poster"));
            }
        });
        button.appendChild(mosaic);
        const text = el("span", "sl-genre-text");
        text.appendChild(el("span", "sl-genre-name", tile.name));
        text.appendChild(el("span", "sl-genre-count", `${tile.total} ${tile.total === 1 ? "title" : "titles"}`));
        button.appendChild(text);
        button.addEventListener("click", (event) => {
            event.preventDefault();
            navigate(tile.href);
        });
        return button;
    }

    // Typo tolerance: edit distance, with how many slips a word of this length may have.
    function editDistance(a, b, max) {
        if (Math.abs(a.length - b.length) > max) {
            return max + 1;
        }
        let previous = Array.from({ length: b.length + 1 }, (_, i) => i);
        for (let i = 1; i <= a.length; i++) {
            const current = [i];
            let best = i;
            for (let j = 1; j <= b.length; j++) {
                current[j] = Math.min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
                best = Math.min(best, current[j]);
            }
            if (best > max) {
                return max + 1;
            }
            previous = current;
        }
        return previous[b.length];
    }

    const allowedTypos = (length) => (length >= 9 ? 2 : length >= 5 ? 1 : 0);

    // Whether the term is (close to) a run of words in the text: "musicl" ~ "musicals",
    // "jams bond" ~ "james bond". Plurals count ("superheros" ~ "superheroes").
    function fuzzyContains(text, term, most) {
        const allowed = Math.min(allowedTypos(term.length), most === undefined ? 2 : most);
        if (!allowed) {
            return false;
        }
        const words = text.split(" ");
        const size = term.split(" ").length;
        for (let i = 0; i + size <= words.length; i++) {
            const run = words.slice(i, i + size).join(" ");
            if (editDistance(run, term, allowed) <= allowed || editDistance(run.replace(/(es|s)$/, ""), term.replace(/(es|s)$/, ""), allowed) <= allowed) {
                return true;
            }
        }
        return false;
    }

    // Dutch search words for genres, sub-genres and collections, and their English names.
    const DUTCH_TERMS = {
        actie: "action", avontuur: "adventure", avonturen: "adventure", komedie: "comedy", komedies: "comedy", komisch: "comedy", grappig: "comedy",
        misdaad: "crime", fantasie: "fantasy", sciencefiction: "science fiction", sf: "science fiction", romantiek: "romance", romantisch: "romance",
        romantische: "romance", liefde: "romance", griezel: "horror", eng: "horror", spanning: "thriller", spannend: "thriller", oorlog: "war",
        geschiedenis: "history", historisch: "history", historische: "history", documentaire: "documentary", documentaires: "documentary", docu: "documentary",
        animatie: "animation", tekenfilm: "animation", familie: "family", gezin: "family", kinderen: "family", kinder: "family", kids: "family",
        muziek: "music", mysterie: "mystery", superheld: "superhero", superhelden: "superheroes", kerst: "christmas", kerstmis: "christmas",
        feestdagen: "christmas", natuur: "nature documentaries", natuurdocumentaire: "nature documentaries", natuurdocumentaires: "nature documentaries",
        ruimte: "space", ruimtevaart: "space", rechtbank: "legal dramas", advocaat: "legal dramas", advocaten: "legal dramas", ziekenhuis: "medical dramas",
        dokter: "medical dramas", artsen: "medical dramas", politiek: "political dramas", waargebeurd: "based on a true story", kostuumdrama: "period dramas",
        politie: "cops and detectives", moord: "whodunits", moordmysterie: "whodunits", opgroeien: "coming of age", tiener: "coming of age",
        tieners: "coming of age", bovennatuurlijk: "supernatural", spook: "supernatural", spoken: "supernatural", dystopisch: "dystopian",
        misdaaddrama: "crime dramas", misdaadthriller: "crime thrillers", feelgood: "feel good", klassieker: "classics", klassiekers: "classics",
        boek: "book", boeken: "book", verfilming: "book", verfilmingen: "book", roman: "book", romans: "book",
        tachtig: "80s", negentig: "90s", prijswinnend: "award", prijswinnende: "award", bekroond: "award", bekroonde: "award", videogame: "video game",
        game: "video game", games: "video game", kort: "short", korte: "short", regisseur: "director",
        // Kinds, kept as a last word so "oorlogsfilms" means war movies.
        films: "movies", film: "movies", series: "shows", serie: "shows", programma: "shows", programmas: "shows"
    };
    const DUTCH_PHRASES = {
        "science fiction": "science fiction", "zwarte komedie": "dark comedies", "romantische komedie": "romantic comedies",
        "romantisch drama": "romantic dramas", "waar gebeurd": "based on a true story", "historisch drama": "period dramas",
        "psychologische thriller": "psychological thrillers", "jaren 80": "80s", "jaren tachtig": "80s", "jaren 90": "90s", "jaren negentig": "90s",
        "jaren 00": "00s", "jaren 2000": "00s", "jaren 10": "10s", "jaren 2010": "10s", "jaren 20": "20s", "jaren 2020": "20s", "korte films": "short"
    };
    const DUTCH_KINDS = ["films", "film", "series", "serie"];

    // The search in English, so Dutch genre and collection words find the English ones:
    // "oorlogsfilms" -> "war movies", "romantische komedie" -> "romantic comedies". Words it
    // doesn't know stay as they are.
    function englishTerm(term) {
        const words = searchWords(term).split(" ").filter(Boolean);
        const out = [];
        let kind = "";
        for (let i = 0; i < words.length; i++) {
            const pair = words.slice(i, i + 2).join(" ");
            if (words[i + 1] && DUTCH_PHRASES[pair]) {
                out.push(DUTCH_PHRASES[pair]);
                i++;
                continue;
            }
            const word = words[i];
            if (DUTCH_PHRASES[word] || (DUTCH_TERMS[word] && !DUTCH_KINDS.includes(word))) {
                out.push(DUTCH_PHRASES[word] || DUTCH_TERMS[word]);
                continue;
            }
            if (DUTCH_KINDS.includes(word)) {
                kind = DUTCH_TERMS[word];
                continue;
            }
            // Glued compounds: "kerstfilms", "oorlogsfilms", "natuurseries"; and plain plurals
            // of known words ("tekenfilms").
            const suffix = DUTCH_KINDS.find((end) => word.endsWith(end) && word.length > end.length + 2);
            const base = suffix ? word.slice(0, -suffix.length) : word;
            const known = [word, word.replace(/s$/, ""), base, base.replace(/s$/, ""), base.replace(/(en|s)$/, "")]
                .find((form) => DUTCH_TERMS[form] && !DUTCH_KINDS.includes(form));
            if (known) {
                out.push(DUTCH_TERMS[known]);
                kind = suffix && known !== word && known !== word.replace(/s$/, "") ? DUTCH_TERMS[suffix] : kind;
            } else {
                out.push(word);
            }
        }
        return out.concat(kind ? [kind] : []).join(" ");
    }

    // Every title, for the typo pass when the normal search finds (almost) nothing.
    function loadTitleIndex() {
        return cached("title-index", STABLE_ROW_TTL_MS, async () => {
            // Names only: no images, user data or extra fields, so it stays small on a big library.
            const result = await getJson("Items", { UserId: userId(), Recursive: true, IncludeItemTypes: "Movie,Series", Fields: "", EnableImages: false, EnableUserData: false, EnableTotalRecordCount: false, Limit: 20000 });
            return ((result && result.Items) || []).map((item) => ({ id: item.Id, words: searchWords(item.Name) }));
        });
    }

    async function fuzzyTitles(term, max) {
        const words = searchWords(term);
        if (allowedTypos(words.length) === 0) {
            return [];
        }
        const index = await loadTitleIndex().catch(() => []);
        const hits = index.filter((entry) => fuzzyContains(entry.words, words)).slice(0, max).map((entry) => entry.id);
        return itemsByIds(hits).catch(() => []);
    }

    // Search words: lowercase, accents and punctuation gone, "&" as "and".
    function searchWords(text) {
        return String(text || "").normalize("NFD").replace(/[\u0300-\u036f]/g, "").toLowerCase()
            .replace(/&/g, " and ").replace(/[^a-z0-9]+/g, " ").trim();
    }

    // What someone types for a genre: "superhero", "musicals", "war movies", "sci fi shows".
    function genreTerm(term) {
        return searchWords(term).replace(/ (movies?|films?|shows?|series)$/, "");
    }

    // "war movies" means movies, "anime shows" shows.
    function termTypes(term) {
        const words = searchWords(term);
        return / (movies?|films?)$/.test(words) ? "Movie" : / (shows?|series)$/.test(words) ? "Series" : "Movie,Series";
    }

    function nameMatches(name, term) {
        const n = searchWords(name);
        if (!term || term.length < 3) {
            return false;
        }
        return n === term || n.startsWith(term) || (term.length >= 4 && n.split(" ").some((word) => word.startsWith(term)))
            || (term.length >= 5 && n.startsWith(term.replace(/(y|ies|s)$/, "")))
            || fuzzyContains(n, term);
    }

    // Genres and sub-genres a search matches, best first (exact name, then name, then keyword).
    function matchGenres(rawTerm) {
        const term = genreTerm(rawTerm);
        if (term.length < 3) {
            return [];
        }
        const genres = new Map();
        mainGenres().concat(String(state.config.HeadlineGenres || "").split(",").map((name) => canonicalGenres(name)[0] || ""), Object.values(TAG_GENRE_NAMES), state.genreNames || [])
            .map((name) => name.trim()).filter(Boolean)
            .forEach((name) => genres.set(name.toLowerCase(), name));
        const matches = [];
        genres.forEach((name) => {
            const label = genreLabel(name);
            if (nameMatches(name, term) || nameMatches(label, term)) {
                matches.push({ name, rank: searchWords(label) === term || searchWords(name) === term ? 0 : 1 });
            }
        });
        SUBGENRES.forEach((def) => {
            if (nameMatches(def.name, term)) {
                matches.push({ name: def.name, def, rank: searchWords(def.name) === term ? 0 : 1 });
            } else if ((def.tags || []).some((tag) => searchWords(tag) === term || (term.length >= 6 && fuzzyContains(searchWords(tag), term, 1)))) {
                matches.push({ name: def.name, def, rank: 2 });
            }
        });
        return matches.sort((x, y) => x.rank - y.rank).slice(0, 4);
    }

    function collectionMatches(collection, rawTerm) {
        const term = searchWords(rawTerm);
        const names = [collection.Name].concat(collection.Aliases || []).map(searchWords);
        if (term.length < 3) {
            // Short ones only exactly: "dc", "007".
            return term.length >= 2 && names.includes(term);
        }
        // Whole words: "bond" finds James Bond and "war" doesn't find Star Wars ("hogwarts").
        // Longer words may be the start of one ("harry"), and the search may hold the whole name
        // ("james bond movies").
        return names.some((name) => ` ${name} `.includes(` ${term} `)
            || (term.length >= 4 && name.split(" ").some((word) => word.startsWith(term)))
            || (name.length >= 3 && ` ${term} `.includes(` ${name} `))
            || fuzzyContains(name, term));
    }

    // Search: one big field, genre shortcuts, results as posters (and people, if any match).
    // Before typing, what's popular here.
    function buildSearchPage(root, view) {
        root.classList.add("sl-root--no-hero", "sl-root--browse", "sl-root--search");
        const head = el("header", "sl-search-head");
        const field = el("label", "sl-search-field");
        field.appendChild(icon("search"));
        const input = el("input", "sl-search-input");
        input.type = "search";
        input.placeholder = "Titles, people, genres, franchises";
        input.autocomplete = "off";
        input.setAttribute("aria-label", "Search");
        field.appendChild(input);
        head.appendChild(field);

        const genres = el("div", "sl-search-genres");
        genres.setAttribute("aria-label", "Browse by genre");
        const headline = String(state.config.HeadlineGenres || state.config.GenreRows || "").split(",").map((name) => name.trim()).filter(Boolean);
        headline.forEach((name) => {
            const chip = el("button", "sl-sort-chip", genreLabel(name));
            chip.type = "button";
            chip.addEventListener("click", () => navigate(browseHref(HOME_SCOPE, { sl: "genre", genre: name })));
            genres.appendChild(chip);
        });
        const all = el("button", "sl-sort-chip sl-sort-chip--more", "All genres");
        all.type = "button";
        all.addEventListener("click", () => navigate(browseHref(HOME_SCOPE, { sl: "genres" })));
        genres.appendChild(all);
        const franchises = el("button", "sl-sort-chip sl-sort-chip--more", "Collections");
        franchises.type = "button";
        franchises.addEventListener("click", () => navigate(collectionsHref()));
        genres.appendChild(franchises);
        // Every genre in the library counts for search (Documentaries, Westerns...), not just
        // the shortcuts.
        cached("home:genre-names", STABLE_ROW_TTL_MS, () => libraryGenreNames(HOME_SCOPE))
            .then((names) => {
                state.genreNames = names;
            })
            .catch(() => { /* the main genres still match */ });
        head.appendChild(genres);

        const results = el("div", "sl-search-results");
        root.appendChild(head);
        root.appendChild(results);
        root.appendChild(buildFooter());

        const pseudoRow = { key: "search", title: "Search", layout: "landscape" };
        let run = 0;
        let timer = 0;

        const grid = (heading, items) => {
            const block = el("section", "sl-search-block");
            if (heading) {
                block.appendChild(el("h2", "sl-row-title sl-search-heading", heading));
            }
            const cards = el("div", "sl-grid");
            items.forEach((item, index) => cards.appendChild(buildCard(item, pseudoRow, index)));
            block.appendChild(cards);
            return block;
        };

        // Before typing: their own recommendations, or what's new if there are none yet.
        const showPopular = async () => {
            const token = ++run;
            await loadFreshness().catch(() => null);
            let heading = "Recommended For You";
            let items = await itemsByIds((await loadRecommendedIds(HOME_SCOPE)).slice(0, 18)).catch(() => []);
            if (!items.length) {
                heading = "New on Jellyfin";
                items = await loadNewItems(HOME_SCOPE, 18).catch(() => []);
            }
            if (token === run) {
                results.replaceChildren(items.length ? grid(heading, items) : el("span"));
            }
        };

        const tileGrid = (heading, tiles, label) => {
            const block = el("section", "sl-search-block");
            block.appendChild(el("h2", "sl-row-title sl-search-heading", heading));
            const list = el("div", "sl-genre-grid sl-search-tiles");
            list.setAttribute("aria-label", label);
            tiles.forEach((tile) => list.appendChild(buildGenreTile(tile)));
            block.appendChild(list);
            return block;
        };

        const search = async (term) => {
            const token = ++run;
            results.replaceChildren(el("div", "sl-grid", null));
            results.firstChild.append(...Array.from({ length: 8 }, () => el("div", "sl-skeleton sl-skeleton--grid")));
            // Dutch genre and collection words count as their English names.
            const english = englishTerm(term);
            const genreMatches = matchGenres(english);
            const types = termTypes(english);
            const genreScope = types === HOME_SCOPE.types ? HOME_SCOPE : Object.assign({}, HOME_SCOPE, { key: `home:${types}`, types, noun: types === "Movie" ? "Movies" : "Shows" });
            const [direct, people, collections] = await Promise.all([
                queryItems({ SearchTerm: term, Limit: 48 }).catch(() => []),
                getJson("Persons", { UserId: userId(), SearchTerm: term, Limit: 10, EnableImageTypes: "Primary", ImageTypeLimit: 1 }).then((r) => (r && r.Items) || []).catch(() => []),
                loadCollections().catch(() => []),
                loadFreshness().catch(() => null)
            ]);

            // A slip of the keyboard: when the title search finds little, titles that are a letter
            // or two off ("intersteller", "jhon wick").
            const items = direct.length >= 4 ? direct : direct.concat((await fuzzyTitles(term, 24)).filter((item) => !direct.some((hit) => hit.Id === item.Id)));

            // Genres and sub-genres by name or keyword ("superhero", "musical", "war movies"):
            // their tiles, and the best one's titles below the title matches.
            const genreTiles = await Promise.all(genreMatches.map((match) => (match.def ? subTileData(genreScope, match.def) : genreTileData(genreScope, match.name)).catch(() => null)));
            const shownGenres = genreTiles.filter((tile) => tile && tile.total > 0);
            const best = genreMatches[genreTiles.indexOf(shownGenres[0])];
            const genreItemsFound = best
                ? byRating(await (best.def ? subgenreItems(genreScope, best.def, 1000) : genreItems(genreScope, best.name, { SortBy: "SortName" }, 1000)).catch(() => []))
                : [];

            // Collections: by name ("james bond", "marvel"), because a title in it was found
            // ("skyfall"), or because most of it is in the genre that was found ("superhero").
            const found = new Set(normalizeIds(items.map((item) => item.Id)));
            const inGenre = new Set(normalizeIds(genreItemsFound.map((item) => item.Id)));
            // Matched by its own name before matched by an alias: "x-men" puts X-Men before Marvel.
            const ownName = (collection) => collectionMatches({ Name: collection.Name }, term) || collectionMatches({ Name: collection.Name }, english);
            const byName = collections.filter((collection) => collectionMatches(collection, term) || (english !== searchWords(term) && collectionMatches(collection, english)))
                .sort((x, y) => ownName(y) - ownName(x));
            const byNameFound = byName.length > 0;
            // A found title only brings in its franchise or movie series ("skyfall": James Bond),
            // not every theme or director it's also part of.
            const related = collections.filter((collection) => !byName.includes(collection)
                && ((["universe", "collection"].includes(collection.Kind) && collection.ItemIds.some((id) => found.has(id)))
                    // By genre ("superhero": Marvel, DC) only when nothing matched by name, so
                    // "marvel" doesn't bring in DC.
                    || (!byNameFound && inGenre.size && !collection.Partial && !isBroadCollection(collection) && collection.ItemIds.filter((id) => inGenre.has(id)).length * 2 >= collection.ItemIds.length)));
            const shownCollections = byName.concat(related).slice(0, 8);
            const members = (await Promise.all(byName.slice(0, 3).map(collectionMembers))).flat();
            if (token !== run) {
                return;
            }

            // Title matches first, then what's in a collection found by name, then the genre.
            const seen = new Set();
            const all = items.concat(members, genreItemsFound).filter((item) => {
                const id = normalizeIds([item.Id])[0];
                return !seen.has(id) && seen.add(id);
            }).slice(0, 60);

            // People first, then collections, genres, and the titles.
            const blocks = [];
            if (people.length) {
                const block = el("section", "sl-search-block");
                block.appendChild(el("h2", "sl-row-title sl-search-heading", "People"));
                const list = el("div", "sl-people");
                people.forEach((person) => {
                    const chip = el("button", "sl-person");
                    chip.type = "button";
                    const face = el("span", "sl-person-face");
                    const src = person.ImageTags && person.ImageTags.Primary ? imageUrl(person.Id, "Primary", person.ImageTags.Primary, { maxHeight: 120 }) : "";
                    if (src) {
                        face.appendChild(lazyImage(src, "sl-person-img"));
                    } else {
                        face.appendChild(icon("person"));
                    }
                    chip.appendChild(face);
                    chip.appendChild(el("span", "sl-person-name", person.Name));
                    chip.addEventListener("click", () => showDetails(person));
                    list.appendChild(chip);
                });
                block.appendChild(list);
                blocks.push(block);
            }
            if (shownCollections.length) {
                blocks.push(tileGrid("Collections", await collectionTiles(shownCollections), "Collections"));
            }
            if (shownGenres.length) {
                blocks.push(tileGrid("Genres", shownGenres, "Genres"));
            }
            if (token !== run) {
                return;
            }
            if (all.length) {
                blocks.push(grid(blocks.length ? "Movies & Shows" : null, all));
            }
            if (!blocks.length) {
                blocks.push(el("p", "sl-empty", `Nothing found for \u201c${term}\u201d. Try another title, a name, a genre or a franchise.`));
            }
            results.replaceChildren(...blocks);
        };

        input.addEventListener("input", () => {
            window.clearTimeout(timer);
            const term = input.value.trim();
            timer = window.setTimeout(() => (term.length ? search(term) : showPopular()), 250);
        });

        const initial = view.params.get("query") || "";
        input.value = initial;
        if (initial) {
            search(initial);
        } else {
            showPopular();
        }
        window.setTimeout(() => input.focus({ preventScroll: true }), 50);
    }

    function destroyRoot(root) {
        if (root.slHero) {
            window.clearInterval(root.slHero.timer);
            root.slHero = null;
        }
        root.remove();
    }

    function clearPageClasses() {
        document.documentElement.classList.remove("sl-active", "sl-home", "sl-hero-page", "sl-scrolled", "sl-pending");
    }

    function syncPageClasses() {
        const view = state.view;
        const root = state.root;
        const html = document.documentElement;
        html.classList.add("sl-active");
        html.classList.toggle("sl-home", !!view && view.kind === "home");
        html.classList.toggle("sl-hero-page", !!root && !root.classList.contains("sl-root--no-hero"));
        html.classList.remove("sl-pending");
    }

    function activate(root, view, fresh) {
        if (state.root && state.root !== root && state.root.slHero) {
            pauseHero("away", state.root.slHero);
        }
        state.root = root;
        state.hero = root.slHero || null;
        view.container.classList.add("sl-takeover");
        syncPageClasses();
        updatePull(view.container);
        updateScrolled();
        if (!fresh) {
            refreshVolatileRows(root);
            resumeHero("away", root.slHero);
        }
    }

    async function mount(view) {
        try {
            await mountPage(view);
        } catch (error) {
            giveUp(error);
        }
    }

    async function mountPage(view) {
        const config = await loadConfig();
        if (state.view !== view || !view.container.isConnected) {
            return;
        }

        if (!config || config.Enabled === false || (view.kind !== "home" && config.LibraryPagesEnabled === false)) {
            view.container.classList.remove("sl-takeover");
            clearPageClasses();
            return;
        }

        document.documentElement.style.setProperty("--sl-accent", config.AccentColor || DEFAULT_ACCENT);

        const existing = view.container.querySelector(":scope > .sl-root");
        if (existing && existing.slKey === view.key) {
            view.hasRoot = true;
            activate(existing, view, false);
            return;
        }
        if (existing) {
            destroyRoot(existing);
        }

        // "animatedScrollX" has no styling anywhere in jellyfin-web; its only effect is that
        // the swipe-between-tabs handler ignores touches starting inside it. Without it, swiping
        // the banner or a row could flip to a neighbouring (possibly hidden) tab.
        const root = el("div", "sl-root animatedScrollX");
        root.slKey = view.key;
        view.hasRoot = true;
        view.container.insertBefore(root, view.container.firstChild);
        state.root = root;
        if (view.kind === "search") {
            buildSearchPage(root, view);
        } else if (view.kind === "browse") {
            buildBrowsePage(root, view);
        } else {
            buildRowsPage(root, view);
        }
        activate(root, view, true);
    }

    function onViewHidden(replaced) {
        closePreview(true);
        if (state.root && state.root.slHero) {
            pauseHero("away", state.root.slHero);
        }
        // When another of our pages takes over straight away, its classes replace these in the
        // same frame (predictPageClasses); clearing first would flash Jellyfin's header.
        if (!replaced) {
            clearPageClasses();
        }
    }

    // Whether the URL is one of the pages we take over (and this device/server wants that).
    function isOurRoute() {
        if (storageGet(ENABLED_KEY) === "0") {
            return false;
        }
        const hash = window.location.hash;
        const libraries = !(state.config && state.config.LibraryPagesEnabled === false);
        return hash.startsWith("#/home") || hash.startsWith("#/search")
            || (libraries && (hash.startsWith("#/movies") || hash.startsWith("#/tv")
                || (hash.startsWith("#/list") && hashParams().get("sl") !== null)));
    }

    // The header classes a page will end up with, applied as soon as it's detected so the
    // header looks right before the page itself has been built.
    function predictPageClasses(view) {
        if (storageGet(ENABLED_KEY) === "0") {
            return;
        }
        const html = document.documentElement;
        html.classList.add("sl-active");
        html.classList.toggle("sl-home", view.kind === "home");
        const hero = view.kind !== "browse" && view.kind !== "search" && !(state.config && state.config.HeroEnabled === false);
        html.classList.toggle("sl-hero-page", hero);
    }

    function isVisible(element) {
        // offsetParent is null while the element (or any ancestor) is display:none, which is
        // how the router hides the pages it keeps around in the background.
        return !!element && element.offsetParent !== null;
    }

    function visibleHomeTab() {
        // There can be more than one: going Home from the menu sometimes builds a fresh home
        // page while the old one is kept around hidden.
        return Array.from(document.querySelectorAll(HOME_TAB_SELECTOR)).find(isVisible) || null;
    }

    // The route is checked too: while switching from Movies to Shows, the URL already points
    // at the Shows library for a moment while the Movies page is still the one showing.
    const LIBRARY_TABS = [
        { route: "#/movies", selector: "#moviesPage #moviesTab.is-active", types: "Movie", noun: "Movies" },
        { route: "#/tv", selector: "#tvRecommendedPage #seriesTab.is-active", types: "Series", noun: "Shows" }
    ];

    // Which streamlined page, if any, is showing right now.
    function detectView() {
        const homeTab = visibleHomeTab();
        if (homeTab) {
            return { kind: "home", key: "home", container: homeTab, scope: HOME_SCOPE };
        }

        const params = hashParams();
        for (const library of LIBRARY_TABS) {
            if (!window.location.hash.startsWith(library.route)) {
                continue;
            }
            const tab = Array.from(document.querySelectorAll(library.selector)).find(isVisible);
            const parentId = params.get("topParentId");
            if (tab && parentId) {
                const scope = { kind: "library", key: `lib:${parentId}:${library.types}`, parentId, types: library.types, noun: library.noun };
                return { kind: "library", key: scope.key, container: tab, scope };
            }
        }

        if (window.location.hash.startsWith("#/search")) {
            const page = Array.from(document.querySelectorAll("#searchPage, .searchPage")).find(isVisible);
            if (page) {
                // Keyed by the query too: opening search again with another one (Jellyfin's own
                // links do) builds a fresh page instead of showing the last results.
                return { kind: "search", key: `search:${params.get("query") || ""}`, container: page, params };
            }
        }

        if (window.location.hash.startsWith("#/list") && params.get("sl")) {
            const page = Array.from(document.querySelectorAll(".page.libraryPage")).find(isVisible);
            if (page) {
                return { kind: "browse", key: `browse:${window.location.hash}`, container: page, params };
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Item pages (Jellyfin's own details page): our rating button next to its buttons, the
    // household rating in the info line, and an embedded trailer above the cast.
    // ---------------------------------------------------------------------------------------

    function youTubeId(link) {
        const match = /(?:youtube(?:-nocookie)?\.com\/(?:watch\?(?:.*&)?v=|embed\/|shorts\/)|youtu\.be\/)([\w-]{11})/.exec(link || "");
        return match ? match[1] : null;
    }

    function buildTrailer(item, trailers) {
        const section = el("div", "verticalSection detailVerticalSection sl-detail-extra sl-trailer");
        section.dataset.item = item.Id;
        const head = el("div", "sl-trailer-head");
        head.appendChild(el("h2", "sectionTitle sl-trailer-title", "Trailer"));
        section.appendChild(head);

        const name = item.Type === "Episode" || item.Type === "Season" ? item.SeriesName || item.Name : item.Name;
        if (!trailers.length) {
            // None anywhere (TMDb, KinoCheck, YouTube): the server keeps looking on its own, so
            // there's nothing for the user to do; leave the section out. The hidden marker just
            // stops this page asking again on every redraw.
            section.hidden = true;
            return section;
        }

        const frame = el("div", "sl-trailer-frame");
        const external = el("a", "sl-trailer-external");
        external.target = "_blank";
        external.rel = "noopener";
        external.appendChild(icon("open_in_new"));
        external.appendChild(el("span", null, "Watch on YouTube"));

        // Nothing is loaded from YouTube except the thumbnail until someone presses play.
        const show = (trailer) => {
            const facade = el("button", "sl-trailer-facade");
            facade.type = "button";
            facade.setAttribute("aria-label", `Play ${trailer.Title || `the trailer for ${name}`}`);
            const img = el("img", "sl-trailer-thumb");
            img.alt = "";
            img.loading = "lazy";
            img.src = `https://i.ytimg.com/vi/${trailer.Key}/hqdefault.jpg`;
            facade.appendChild(img);
            const play = el("span", "sl-trailer-play");
            play.appendChild(icon("play_arrow"));
            facade.appendChild(play);
            facade.addEventListener("click", () => {
                const iframe = el("iframe", "sl-trailer-iframe");
                iframe.src = `https://www.youtube-nocookie.com/embed/${trailer.Key}?autoplay=1&rel=0&modestbranding=1&playsinline=1&origin=${encodeURIComponent(window.location.origin)}`;
                iframe.title = trailer.Title || `Trailer: ${name}`;
                iframe.allow = "autoplay; encrypted-media; picture-in-picture; fullscreen";
                // Jellyfin's page sets referrer policy "no-referrer", and YouTube refuses to play
                // embeds that arrive without one (error 153). Override it for this frame only,
                // sending just this server's address.
                iframe.referrerPolicy = "strict-origin-when-cross-origin";
                iframe.allowFullscreen = true;
                frame.replaceChildren(iframe);
            });
            frame.replaceChildren(facade);
            external.href = `https://www.youtube.com/watch?v=${trailer.Key}`;
            section.slCurrent = trailer;
        };

        if (trailers.length > 1) {
            const picks = el("div", "sl-trailer-picks");
            trailers.forEach((trailer, index) => {
                // A show's season trailers say which season; the rest go by their title.
                const label = trailer.Season ? `Season ${trailer.Season}` : trailer.Title ? shortTrailerTitle(trailer.Title, name) : `Trailer ${index + 1}`;
                const chip = el("button", "sl-sort-chip", label);
                chip.type = "button";
                chip.title = trailer.Title || label;
                chip.classList.toggle("is-on", index === 0);
                chip.addEventListener("click", () => {
                    picks.querySelectorAll(".sl-sort-chip").forEach((other) => other.classList.toggle("is-on", other === chip));
                    show(trailer);
                });
                picks.appendChild(chip);
            });
            head.appendChild(picks);
        }

        section.appendChild(frame);
        section.appendChild(external);
        show(trailers[0]);
        section.slReset = () => show(section.slCurrent);
        return section;
    }

    // "Dune: Part Two | Official Trailer 3" -> "Official Trailer 3": the title's already on the page.
    function shortTrailerTitle(title, name) {
        let short = title;
        // "FINAL TRAILER | NETFLIX": keep the part that says what it is, drop the channel.
        const parts = short.split(/\s+[|\u2022]\s+/);
        if (parts.length > 1) {
            short = parts.find((part) => /trailer|teaser/i.test(part)) || parts[0];
        }
        // All caps reads as shouting next to the other buttons.
        if (short === short.toUpperCase() && /[A-Z]{3}/.test(short)) {
            short = short.toLowerCase().replace(/\b\w/g, (letter) => letter.toUpperCase());
        }
        const at = short.toLowerCase().indexOf(String(name || "").toLowerCase());
        if (name && at >= 0) {
            short = (short.slice(0, at) + short.slice(at + name.length)).replace(/^[\s|:\-–—]+|[\s|:\-–—]+$/g, "");
        }
        short = short.replace(/\s*\(\d{4}\)\s*/g, " ").replace(/\s{2,}/g, " ").trim() || title;
        return short.length > 40 ? `${short.slice(0, 38)}…` : short;
    }

    function trailerSlot(page) {
        // Right under the summary (before the genres/cast details, Next Up and the seasons).
        const summary = page.querySelector(".detailPagePrimaryContent .detailSectionContent");
        if (summary && summary.parentNode) {
            return { parent: summary.parentNode, before: summary.nextSibling };
        }
        const section = page.querySelector(".detailPagePrimaryContent .detailSection") || page.querySelector(".detailPageContent");
        return section ? { parent: section, before: section.firstChild } : null;
    }

    // A trailer left playing on a page that's no longer showing (the router keeps pages around)
    // would keep playing in the background; put those back to their thumbnail.
    function stopHiddenTrailers() {
        document.querySelectorAll(".sl-trailer").forEach((section) => {
            if (!isVisible(section) && section.querySelector("iframe") && section.slReset) {
                section.slReset();
            }
        });
    }

    let detailsPending = null;

    function enhanceDetails() {
        stopHiddenTrailers();
        if (!window.location.hash.startsWith("#/details")) {
            return;
        }
        const id = hashParams().get("id");
        const page = Array.from(document.querySelectorAll(".itemDetailPage")).find(isVisible);
        const buttons = page && page.querySelector(".mainDetailButtons");
        if (!id || !buttons) {
            return;
        }

        const needRate = !buttons.querySelector(`.sl-detail-extra[data-item="${id}"]`);
        const needPartOf = !page.querySelector(`.sl-more-like[data-item="${id}"]`);
        const needTrailer = state.config.TrailersEnabled !== false && !!trailerSlot(page) && !page.querySelector(`.sl-trailer[data-item="${id}"]`)
            && page.dataset.slNoTrailer !== id;
        const key = `${id}:${needRate}:${needTrailer}:${needPartOf}`;
        if ((!needRate && !needTrailer && !needPartOf) || detailsPending === key) {
            return;
        }
        detailsPending = key;

        cached(`item:${id}`, 60000, () => ApiClient.getItem(userId(), id))
            .then((item) => {
                detailsPending = null;
                if (!page.isConnected || hashParams().get("id") !== id) {
                    return;
                }
                if (needRate && !buttons.querySelector(`.sl-detail-extra[data-item="${id}"]`)) {
                    buttons.querySelectorAll(".sl-detail-extra").forEach((node) => node.remove());
                    // Same round buttons as everywhere else: + Watch List, heart, rate, and the
                    // double check for watched. Jellyfin's own check and heart are hidden (CSS),
                    // so there aren't two check marks side by side. An episode or season puts
                    // its show on the Watch List.
                    buttons.classList.add("sl-detail-buttons");
                    const group = el("span", "sl-detail-extra sl-detail-group");
                    group.dataset.item = id;
                    const listId = item.Type === "Movie" || item.Type === "Series" ? item.Id : item.SeriesId;
                    loadWatchlist().catch(() => null).then(() => {
                        if (listId) {
                            group.appendChild(listButton(listId, "sl-round sl-round--hero"));
                        }
                        // Favorites are for movies and whole shows, not single episodes/seasons.
                        if (item.Type === "Movie" || item.Type === "Series") {
                            group.appendChild(favoriteButton(item, "sl-round sl-round--hero"));
                        }
                        group.appendChild(rateButton(item, "sl-round sl-round--hero"));
                        group.appendChild(playedButton(item, "sl-round sl-round--hero"));
                    });
                    // Before the ⋯ button when it's one of the row's own buttons, else at the end.
                    const more = Array.from(buttons.children).find((child) => child.classList.contains("btnMoreCommands")) || null;
                    buttons.insertBefore(group, more);
                }
                if (needPartOf && ["Movie", "Series", "Season", "Episode"].includes(item.Type)) {
                    addMoreLike(page, item, id).catch(() => { /* the page is fine without it */ });
                }
                // Trailers on movie and show pages only, not on seasons or episodes.
                if (needTrailer && !["Movie", "Series"].includes(item.Type)) {
                    page.dataset.slNoTrailer = id;
                }
                if (needTrailer && ["Movie", "Series"].includes(item.Type)) {
                    cached(`trailers:${id}`, STABLE_ROW_TTL_MS, () => getJson(`Streamline/Trailers/${id}`))
                        .catch(() => [])
                        .then((trailers) => {
                            const slot = trailerSlot(page);
                            if (!slot || !page.isConnected || hashParams().get("id") !== id || page.querySelector(`.sl-trailer[data-item="${id}"]`)) {
                                return;
                            }
                            page.querySelectorAll(".sl-trailer").forEach((node) => node.remove());
                            const before = slot.before && slot.before.parentNode === slot.parent ? slot.before : null;
                            slot.parent.insertBefore(buildTrailer(item, trailers || []), before);
                            // Jellyfin's own Trailer button would just open the same trailer.
                            buttons.classList.toggle("sl-has-trailer", !!(trailers && trailers.length));
                        });
                }
            })
            .catch(() => {
                detailsPending = null;
            });
    }

    // At the bottom of an item page: "More like this", the collections the title is part of and
    // its first two genres (approved ones), each a button to go and look. An episode or season
    // uses its show. Not the broad collections like the decades.
    async function addMoreLike(page, item, id) {
        const titleId = item.Type === "Movie" || item.Type === "Series" ? item.Id : item.SeriesId;
        if (!titleId) {
            return;
        }
        const [collections, title] = await Promise.all([
            cached(`partof:${normalizeIds([titleId])[0]}`, STABLE_ROW_TTL_MS, () => getJson(`Streamline/Collections/Of/${titleId}`)).catch(() => []),
            titleId === item.Id ? item : cached(`item:${titleId}`, 60000, () => ApiClient.getItem(userId(), titleId)).catch(() => null)
        ]);
        const bottom = page.querySelector(".detailPageSecondaryContainer .detailPageContent") || page.querySelector(".detailPageContent");
        if (!bottom || !page.isConnected || hashParams().get("id") !== id || page.querySelector(`.sl-more-like[data-item="${id}"]`)) {
            return;
        }
        const order = { universe: 0, collection: 1, featured: 2, director: 3, tv: 4, anime: 5 };
        const found = (collections || [])
            .filter((collection) => !isBroadCollection(collection))
            .sort((x, y) => order[x.Kind] - order[y.Kind])
            .slice(0, 4);
        const type = title && title.Type === "Series" ? "Series" : "Movie";
        const scope = Object.assign({}, HOME_SCOPE, { key: `home:${type}`, types: type, noun: type === "Series" ? "Shows" : "Movies" });
        const genres = title ? Array.from(new Set((title.Genres || []).flatMap(canonicalGenres))).slice(0, 2) : [];
        page.querySelectorAll(".sl-more-like").forEach((node) => node.remove());
        if (!found.length && !genres.length) {
            return;
        }

        const section = el("div", "verticalSection detailVerticalSection sl-detail-extra sl-more-like");
        section.dataset.item = id;
        // Jellyfin has its own "More Like This" (posters) just above; this one is "Explore".
        section.appendChild(el("h2", "sectionTitle sl-more-like-title", "Explore"));
        const buttons = el("div", "sl-more-like-buttons");
        const add = (iconName, label, sub, href) => {
            const link = el("a", "sl-more-like-btn");
            link.href = `#${href}`;
            link.appendChild(icon(iconName));
            const text = el("span", "sl-more-like-text");
            text.appendChild(el("span", "sl-more-like-label", label));
            text.appendChild(el("span", "sl-more-like-sub", sub));
            link.appendChild(text);
            link.addEventListener("click", (event) => {
                event.preventDefault();
                navigate(href);
            });
            buttons.appendChild(link);
        };
        found.forEach((collection) => add(
            collection.Kind === "director" ? "movie_filter" : "video_library",
            collection.Kind === "director" ? `More from ${collection.Name}` : collection.Name,
            collection.Kind === "universe" ? "Franchise" : collection.Kind === "director" ? "Director" : "Collection",
            collectionHref(collection)));
        genres.forEach((genre) => add("category", genreLabel(genre), `More ${scope.noun.toLowerCase()}`, browseHref(scope, { sl: "genre", genre })));
        section.appendChild(buttons);
        bottom.appendChild(section);
    }

    // ---------------------------------------------------------------------------------------
    // Admins: "Trailer..." in an item page's ⋯ menu, to set the right trailer by hand when the
    // automatic one is wrong (or none), and to go back to automatic.
    // ---------------------------------------------------------------------------------------

    let moreMenuOpenedAt = 0;
    document.addEventListener("click", (event) => {
        if (event.target.closest && event.target.closest(".itemDetailPage .btnMoreCommands")) {
            moreMenuOpenedAt = Date.now();
        }
    }, true);

    function addTrailerMenuItem() {
        if (!state.isAdmin || Date.now() - moreMenuOpenedAt > 3000 || !window.location.hash.startsWith("#/details")) {
            return;
        }
        const sheet = document.querySelector(".actionSheet.opened");
        const list = sheet && sheet.querySelector(".actionSheetMenuItem") && sheet.querySelector(".actionSheetMenuItem").parentElement;
        if (!list || list.querySelector(".sl-trailer-menu")) {
            return;
        }
        const id = hashParams().get("id");
        // Only where there's a trailer section (movies and shows).
        if (!document.querySelector(`.itemDetailPage .sl-trailer[data-item="${id}"]`)) {
            return;
        }
        const entry = el("button", "listItem listItem-button actionSheetMenuItem emby-button sl-trailer-menu");
        entry.type = "button";
        const glyph = el("span", "actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons movie");
        glyph.setAttribute("aria-hidden", "true");
        entry.appendChild(glyph);
        const body = el("div", "listItemBody actionsheetListItemBody");
        body.appendChild(el("div", "listItemBodyText actionSheetItemText", "Trailer..."));
        entry.appendChild(body);
        entry.addEventListener("click", (event) => {
            event.preventDefault();
            event.stopPropagation();
            // Close Jellyfin's menu the way a click next to it does (or, failing that, the way
            // Back does), then open ours.
            const container = sheet.closest(".dialogContainer");
            if (container) {
                container.dispatchEvent(new MouseEvent("click", { bubbles: true }));
            }
            window.setTimeout(() => {
                if (sheet.isConnected && sheet.classList.contains("opened")) {
                    window.history.back();
                }
                window.setTimeout(() => openTrailerDialog(id), 250);
            }, 200);
        });
        list.appendChild(entry);
    }

    async function openTrailerDialog(id) {
        if (!id || document.querySelector(".sl-dialog")) {
            return;
        }
        const info = await getJson(`Streamline/Admin/Trailers/${id}`).catch(() => null);
        if (!info) {
            return;
        }
        const overlay = el("div", "sl-dialog");
        const panel = el("div", "sl-dialog-panel");
        panel.setAttribute("role", "dialog");
        panel.setAttribute("aria-modal", "true");
        panel.setAttribute("aria-label", "Trailer");
        overlay.appendChild(panel);
        panel.appendChild(el("h2", "sl-dialog-title", "Trailer"));
        panel.appendChild(el("p", "sl-dialog-sub", `For ${info.TargetName}. Only admins can change this; it's what everyone sees.`));
        const choice = info.Choice;
        panel.appendChild(el("p", "sl-trailer-now", choice ? (choice.None ? "Now: no trailer (your choice)." : `Now: ${choice.Title || choice.Key} (your choice).`) : "Now: found automatically."));

        const field = el("input", "sl-trailer-input");
        field.type = "url";
        field.placeholder = "YouTube link, like https://www.youtube.com/watch?v=...";
        field.setAttribute("aria-label", "YouTube link");
        panel.appendChild(field);
        const message = el("p", "sl-trailer-message");
        panel.appendChild(message);

        const close = () => {
            overlay.remove();
            document.removeEventListener("keydown", onKey);
        };
        const onKey = (event) => {
            if (event.key === "Escape") {
                close();
            }
        };
        const done = () => {
            close();
            invalidate(`trailers:${id}`);
            document.querySelectorAll(".sl-trailer").forEach((node) => node.remove());
            enhanceDetails();
        };
        const send = async (type, body) => {
            message.textContent = "";
            try {
                await ApiClient.ajax({ type, url: ApiClient.getUrl(`Streamline/Admin/Trailers/${id}`), data: body ? JSON.stringify(body) : undefined, contentType: "application/json" });
                done();
            } catch (error) {
                message.textContent = "That didn't work: check it's a YouTube link to a video that may be embedded.";
            }
        };

        const footer = el("div", "sl-dialog-footer sl-trailer-footer");
        const button = (label, className, onClick) => {
            const b = el("button", className, label);
            b.type = "button";
            b.addEventListener("click", onClick);
            footer.appendChild(b);
            return b;
        };
        button("Back to automatic", "sl-dialog-text", () => send("DELETE"));
        button("No trailer", "sl-dialog-text", () => send("POST", { None: true }));
        footer.appendChild(el("span", "sl-dialog-spacer"));
        button("Cancel", "sl-dialog-text", close);
        const use = button("Use this video", "sl-dialog-save", () => {
            if (!field.value.trim()) {
                message.textContent = "Paste a YouTube link first.";
                return;
            }
            send("POST", { Url: field.value.trim() });
        });
        field.addEventListener("keydown", (event) => {
            if (event.key === "Enter") {
                use.click();
            }
        });
        panel.appendChild(footer);
        overlay.addEventListener("click", (event) => {
            if (event.target === overlay) {
                close();
            }
        });
        document.addEventListener("keydown", onKey);
        document.body.appendChild(overlay);
        field.focus({ preventScroll: true });
    }

    // Whether the signed-in user is an admin: only admins keep the side menu (with just the
    // Dashboard and the Metadata Manager in it).
    function syncAdmin() {
        cached("me", STABLE_ROW_TTL_MS, () => ApiClient.getCurrentUser())
            .then((user) => {
                state.isAdmin = !!(user && user.Policy && user.Policy.IsAdministrator);
                document.documentElement.classList.toggle("sl-admin", state.isAdmin);
                ensureUserViewButton();
                ensureDashboardHome();
            })
            .catch(() => { /* keep whatever it was */ });
    }

    // ---------------------------------------------------------------------------------------
    // User view (testing, admins): the site signed in as the test account chosen in the
    // settings, in a frame over the page, so the admin sees exactly what that user sees (their
    // Watch List, Favorites, ratings, menus) without signing out. The sign-in comes from
    // Jellyfin's Quick Connect, approved by the admin for that account; the frame keeps it in
    // its own corner of the browser's storage (early.js), so the admin's session is untouched.
    // ---------------------------------------------------------------------------------------

    const IN_USER_VIEW = /[?&]slviewas=1(&|$)/.test(window.location.search);
    const USER_VIEW_PREFIX = "streamline-viewas:";

    function ensureUserViewButton() {
        const onDashboard = document.body.classList.contains("dashboardDocument");
        const target = state.config && state.config.UserView;
        const wanted = state.isAdmin && target && !IN_PHONE_PREVIEW && !IN_USER_VIEW && window.top === window && !onDashboard;
        const existing = document.querySelector(".sl-userview-toggle");
        if (!wanted) {
            if (existing && !state.userView) {
                existing.remove();
            }
            return;
        }
        const label = state.userView ? "Back to your view" : `User view (${target.Name})`;
        if (existing && existing.dataset.label === label) {
            return;
        }
        const button = existing || el("button", "sl-phone-toggle sl-userview-toggle");
        button.type = "button";
        button.dataset.label = label;
        button.replaceChildren(icon(state.userView ? "admin_panel_settings" : "person_outline"), el("span", null, label));
        if (!existing) {
            button.addEventListener("click", () => (state.userView ? closeUserView() : openUserView()));
            document.body.appendChild(button);
        }
    }

    function userViewNotice(text) {
        const note = el("div", "sl-userview-note", text);
        document.body.appendChild(note);
        window.setTimeout(() => note.remove(), 6000);
    }

    async function openUserView() {
        const target = state.config && state.config.UserView;
        if (!target || state.userView) {
            return;
        }
        const button = document.querySelector(".sl-userview-toggle");
        button.disabled = true;
        const deviceId = `streamline-userview-${Math.random().toString(36).slice(2, 10)}`;
        const auth = (token) => `MediaBrowser Client="Streamline", Device="User view", DeviceId="${deviceId}", Version="${ApiClient.appVersion()}"${token ? `, Token="${token}"` : ""}`;
        const post = async (path, body, token) => {
            const response = await fetch(ApiClient.getUrl(path), {
                method: "POST",
                headers: { "Authorization": auth(token), "Content-Type": "application/json" },
                body: body ? JSON.stringify(body) : undefined
            });
            if (!response.ok) {
                throw new Error(`${path}: ${response.status}`);
            }
            const text = await response.text();
            return text ? JSON.parse(text) : null;
        };
        try {
            // Quick Connect: a code for this frame, approved by the admin for the test account.
            const request = await post("QuickConnect/Initiate").catch(() => null);
            if (!request) {
                userViewNotice("User view needs Quick Connect: turn it on under Dashboard > General.");
                return;
            }
            await ApiClient.ajax({ type: "POST", url: ApiClient.getUrl("QuickConnect/Authorize", { code: request.Code, userId: target.Id }) });
            const session = await post("Users/AuthenticateWithQuickConnect", { Secret: request.Secret });

            // The frame's own copy of the sign-in, the same as this page's but for that account.
            const credentials = JSON.parse(window.localStorage.getItem("jellyfin_credentials") || "{\"Servers\":[]}");
            const serverId = ApiClient.serverId();
            const own = (credentials.Servers || []).find((server) => server.Id === serverId) || { Id: serverId, ManualAddress: ApiClient.serverAddress(), LastConnectionMode: 2 };
            const servers = [Object.assign({}, own, { UserId: session.User.Id, AccessToken: session.AccessToken, DateLastAccessed: Date.now() })];
            window.localStorage.setItem(`${USER_VIEW_PREFIX}jellyfin_credentials`, JSON.stringify(Object.assign({}, credentials, { Servers: servers })));
            window.localStorage.setItem(`${USER_VIEW_PREFIX}_deviceId2`, deviceId);

            const overlay = el("div", "sl-userview");
            const bar = el("div", "sl-userview-bar");
            bar.appendChild(icon("person_outline"));
            bar.appendChild(el("span", null, `Viewing as ${session.User.Name}`));
            overlay.appendChild(bar);
            const frame = el("iframe", "sl-userview-frame");
            frame.title = `Viewing as ${session.User.Name}`;
            frame.src = `${window.location.pathname}?slviewas=1#/home`;
            frame.addEventListener("load", () => {
                // Without early.js running first (JavaScript Injector), the frame would just be
                // the admin again: close it rather than pretend.
                if (!frame.contentWindow || !frame.contentWindow.__streamlineUserView) {
                    closeUserView();
                    userViewNotice("User view needs the File Transformation plugin or direct injection (see the injection method setting).");
                }
            }, { once: true });
            overlay.appendChild(frame);
            document.body.appendChild(overlay);
            state.userView = { overlay, token: session.AccessToken, post };
        } catch (error) {
            console.warn("[Streamline] User view failed.", error);
            userViewNotice("Couldn't open the User view. Is the test account still there?");
        } finally {
            button.disabled = false;
            ensureUserViewButton();
        }
    }

    function closeUserView() {
        const view = state.userView;
        if (!view) {
            return;
        }
        state.userView = null;
        view.overlay.remove();
        const phone = document.querySelector(".sl-phone-preview");
        if (phone) {
            togglePhonePreview();
        }
        // Sign the test account's frame out again and forget its storage.
        view.post("Sessions/Logout", null, view.token).catch(() => { /* it expires with the device */ });
        try {
            Object.keys(window.localStorage).filter((key) => key.startsWith(USER_VIEW_PREFIX)).forEach((key) => window.localStorage.removeItem(key));
        } catch {
            /* nothing to tidy */
        }
        ensureUserViewButton();
    }

    // The dashboard's own side menu has no plain way back; a Home item at its top.
    function ensureDashboardHome() {
        if (!document.body.classList.contains("dashboardDocument")) {
            return;
        }
        // The dashboard's own menu, not Jellyfin's (hidden) side menu, which has the same link.
        const first = Array.from(document.querySelectorAll('a[href="#/dashboard"]')).find((link) => !link.closest(".mainDrawer"));
        const list = first && first.closest(".MuiList-root, ul, nav");
        if (!list || list.querySelector(".sl-dash-home")) {
            return;
        }
        const home = el("a", "sl-dash-home");
        home.href = "#/home";
        home.appendChild(icon("home"));
        home.appendChild(el("span", null, "Home"));
        list.insertBefore(home, list.firstChild);
    }

    function closeUserMenu() {
        const menu = document.querySelector(".sl-usermenu");
        if (menu) {
            menu.remove();
        }
    }

    function toggleUserMenu(button) {
        if (document.querySelector(".sl-usermenu")) {
            closeUserMenu();
            return;
        }
        const menu = el("div", "sl-usermenu");
        menu.setAttribute("role", "menu");
        menu.appendChild(el("div", "sl-usermenu-name", button.getAttribute("title") || ""));
        // Jellyfin's own (hidden) side-menu buttons do the actual work, so signing out goes
        // through exactly the same steps as before.
        const item = (label, iconName, selector, fallback) => {
            const entry = el("button", "sl-usermenu-item");
            entry.type = "button";
            entry.setAttribute("role", "menuitem");
            entry.appendChild(icon(iconName));
            entry.appendChild(el("span", null, label));
            entry.addEventListener("click", () => {
                closeUserMenu();
                const native = document.querySelector(`.mainDrawer ${selector}`);
                if (native) {
                    native.click();
                } else {
                    fallback();
                }
            });
            menu.appendChild(entry);
        };
        item("Settings", "settings", ".btnSettings", () => navigate("/mypreferencesmenu"));
        item("Sign out", "logout", ".btnLogout", () => (window.Dashboard && window.Dashboard.logout ? window.Dashboard.logout() : navigate("/login")));
        const rect = button.getBoundingClientRect();
        menu.style.top = `${Math.round(rect.bottom + 6)}px`;
        menu.style.right = `${Math.max(8, Math.round(window.innerWidth - rect.right))}px`;
        document.body.appendChild(menu);
        const away = (event) => {
            if (!menu.contains(event.target) && !(event.target.closest && event.target.closest(".headerUserButton"))) {
                closeUserMenu();
            }
        };
        const escape = (event) => {
            if (event.key === "Escape") {
                closeUserMenu();
            }
        };
        document.addEventListener("pointerdown", away, true);
        document.addEventListener("keydown", escape);
        // Tidy up the listeners once it's gone, however it went.
        new MutationObserver((records, watcher) => {
            if (!menu.isConnected) {
                document.removeEventListener("pointerdown", away, true);
                document.removeEventListener("keydown", escape);
                watcher.disconnect();
            }
        }).observe(document.body, { childList: true });
        menu.querySelector(".sl-usermenu-item").focus({ preventScroll: true });
    }

    // ---------------------------------------------------------------------------------------
    // Back: a small arrow in the top-left corner on every device. Going back (with it, the
    // browser's or the phone's back) returns to the same spot: how far down the page was
    // scrolled, and how far each row was scrolled sideways.
    // ---------------------------------------------------------------------------------------

    const scrollMemory = new Map();
    let restoreToken = 0;

    function rememberedFor(hash) {
        if (!scrollMemory.has(hash)) {
            scrollMemory.set(hash, { y: 0, rows: {} });
        }
        return scrollMemory.get(hash);
    }

    function rememberScroll() {
        rememberedFor(window.location.hash).y = window.scrollY;
    }

    // After a click that leaves the page, the old page is hidden before the address changes,
    // and the browser's clamped scroll position would be remembered as that page's. Keep what was
    // remembered just before the click instead.
    let frozenUntil = 0;
    document.addEventListener("click", () => {
        rememberScroll();
        frozenUntil = Date.now() + 1500;
    }, true);
    window.addEventListener("hashchange", () => {
        frozenUntil = Date.now() + 1500;
    });
    window.addEventListener("scroll", () => {
        if (!restoring && Date.now() > frozenUntil) {
            rememberScroll();
        }
    }, { passive: true });
    // Rows scrolled sideways (the scroll events of the rows themselves).
    document.addEventListener("scroll", (event) => {
        const track = event.target && event.target.classList && event.target.classList.contains("sl-row-track") ? event.target : null;
        const row = track && track.closest(".sl-row");
        if (row && row.dataset.row && !restoring) {
            rememberedFor(window.location.hash).rows[row.dataset.row] = track.scrollLeft;
        }
    }, { capture: true, passive: true });

    let restoring = false;
    window.addEventListener("popstate", () => {
        const memory = scrollMemory.get(window.location.hash);
        if (!memory || (!memory.y && !Object.keys(memory.rows).length)) {
            return;
        }
        // The page may still be building (rows filling in, a grid loading more): keep putting
        // it back for a few seconds until it's tall enough and the rows are there.
        const token = ++restoreToken;
        const hash = window.location.hash;
        const started = Date.now();
        let stableSince = 0;
        restoring = true;
        const step = () => {
            if (token !== restoreToken || window.location.hash !== hash) {
                restoring = false;
                return;
            }
            if (Math.abs(window.scrollY - memory.y) > 2) {
                window.scrollTo(0, memory.y);
            }
            let rowsDone = true;
            Object.entries(memory.rows).forEach(([key, left]) => {
                const track = Array.from(document.querySelectorAll(`.sl-row[data-row="${CSS.escape(key)}"] .sl-row-track`)).find(isVisible);
                if (!track) {
                    rowsDone = false;
                } else if (Math.abs(track.scrollLeft - left) > 2) {
                    track.scrollLeft = left;
                    rowsDone = rowsDone && Math.abs(track.scrollLeft - left) <= 2;
                }
            });
            // Keep it there a moment after reaching it: Jellyfin puts its own idea of the
            // position back a little later on some pages.
            const there = Math.abs(window.scrollY - memory.y) <= 2 && rowsDone;
            stableSince = there ? stableSince || Date.now() : 0;
            if ((!there || Date.now() - stableSince < 1200) && Date.now() - started < 6000) {
                window.setTimeout(step, 100);
            } else {
                restoring = false;
            }
        };
        window.setTimeout(step, 50);
    });
    // Someone scrolling while it's still being put back takes over.
    ["wheel", "touchstart", "keydown"].forEach((type) => window.addEventListener(type, () => {
        if (restoring) {
            restoreToken++;
            restoring = false;
        }
    }, { passive: true }));

    // The back arrow sits just under the logo, and only on pages you went into (a title, a
    // genre, a collection, search...), not on the main pages the header links to.
    function ensureBackButton() {
        // Not inside the header: it clips anything below its own edge.
        if (!document.querySelector(".skinHeader")) {
            return;
        }
        if (!document.querySelector("body > .sl-back")) {
            const back = el("button", "sl-back");
            back.type = "button";
            back.title = "Back";
            back.setAttribute("aria-label", "Back");
            back.appendChild(icon("arrow_back"));
            back.appendChild(el("span", "sl-back-label", "Back"));
            back.addEventListener("click", () => {
                rememberScroll();
                if (window.history.length > 1) {
                    window.history.back();
                } else {
                    navigate("/home");
                }
            });
            document.body.appendChild(back);
        }
        document.documentElement.classList.toggle("sl-main-page", isMainPage());
        placeBackButton();
    }

    // Just under the header, lined up with the logo (desktop) or the page edge (phones, where
    // there's no logo).
    function placeBackButton() {
        const header = document.querySelector(".skinHeader");
        const logo = document.querySelector(".skinHeader .sl-logo");
        const left = logo && isVisible(logo) ? logo.getBoundingClientRect().left : 0;
        const root = document.documentElement.style;
        root.setProperty("--sl-back-left", left > 0 ? `${Math.max(12, Math.round(left))}px` : "");
        root.setProperty("--sl-back-top", header ? `${Math.round(header.getBoundingClientRect().bottom)}px` : "");
    }
    window.addEventListener("resize", placeBackButton, { passive: true });

    // The pages the header links to: Home, the libraries, Watch List, Favorites, Collections.
    function isMainPage() {
        const hash = window.location.hash;
        const params = hashParams();
        if (!hash || hash === "#/" || hash.startsWith("#/home") || visibleHomeTab()) {
            return true;
        }
        if (LIBRARY_TABS.some((library) => hash.startsWith(`${library.route}?`) || hash === library.route) && params.get("topParentId")) {
            return true;
        }
        if (hash.startsWith("#/list")) {
            const sl = params.get("sl");
            return sl === "collections" || (["mylist", "favorites"].includes(sl) && !params.get("parentId"));
        }
        return false;
    }

    function syncNav() {
        loadConfig()
            .then((config) => {
                if (config && config.Enabled !== false) {
                    ensureBackButton();
                    ensureNav();
                    ensureTabBar();
                    ensurePhonePreviewButton();
                    syncAdmin();
                    ensureDashboardHome();
                    updateNav();
                    enhanceDetails();
                    // The video player shares the header; it keeps its own back button.
                    document.documentElement.classList.toggle("sl-nav-on", !window.location.hash.startsWith("#/video"));
                } else {
                    removeNav();
                }
            })
            .catch(() => { /* try again on the next check */ });
    }

    function sameView(a, b) {
        return !!a && !!b && a.key === b.key && a.container === b.container;
    }

    // If anything here goes wrong, step aside for the rest of the visit: Jellyfin's own pages
    // come back as they are, rather than a page we hid and then failed to replace.
    function giveUp(error) {
        if (state.broken) {
            return;
        }
        state.broken = true;
        console.error("[Streamline] Something went wrong; showing Jellyfin's own pages for now.", error);
        try {
            document.querySelectorAll(".sl-root").forEach((root) => root.remove());
            document.querySelectorAll(".sl-takeover").forEach((node) => node.classList.remove("sl-takeover"));
            document.documentElement.classList.remove("sl-route", "sl-active", "sl-home", "sl-hero-page", "sl-scrolled", "sl-pending");
            removeNav();
        } catch {
            /* nothing more to undo */
        }
    }

    function check() {
        if (state.broken) {
            return;
        }
        try {
            addTrailerMenuItem();
            checkPage();
        } catch (error) {
            giveUp(error);
        }
    }

    function checkPage() {
        const uid = userId();
        if (!uid) {
            removeNav();
        }

        if (state.cacheUserId && uid && state.cacheUserId !== uid) {
            // Signed in as someone else: start over with their data.
            document.querySelectorAll(".sl-root").forEach(destroyRoot);
            state.root = null;
            state.hero = null;
            state.view = null;
            state.config = null;
            removeNav();
            clearPageClasses();
            recommendationsRetried = false;
        }

        if (uid) {
            ensureStyle();
            syncNav();
        }

        // Jellyfin's tab rows go by the URL, not by whether our page is built yet, so they never
        // flash in while switching between our pages.
        document.documentElement.classList.toggle("sl-route", !!uid && isOurRoute());

        const view = uid ? detectView() : null;
        if (!view) {
            // Mid-navigation between two of our pages there can be a frame with neither showing;
            // keep the header as it is rather than flashing Jellyfin's in between.
            if (state.view && !isOurRoute()) {
                state.view = null;
                onViewHidden();
            }
            return;
        }

        if (sameView(view, state.view) && state.view.status) {
            // Already settling/building, or built (with or without our page: classic mode and
            // disabled pages build nothing). Only rebuild if our page went missing from it.
            const lost = state.view.status === "done" && state.view.hasRoot && !(state.root && view.container.contains(state.root));
            if (!lost) {
                return;
            }
        }

        if (state.view) {
            onViewHidden(true);
        }
        state.view = view;
        view.status = "pending";
        predictPageClasses(view);

        // Hide the native content right away (unless this device opted out) so it doesn't
        // flash in before the config arrives.
        if (storageGet(ENABLED_KEY) !== "0") {
            view.container.classList.add("sl-takeover");
            document.documentElement.classList.add("sl-pending");
        }

        // Only build once the page has settled: while navigating, the URL can already point at
        // the next page while the previous page element is still the visible one for a moment.
        window.clearTimeout(settleTimer);
        settleTimer = window.setTimeout(() => {
            const settled = detectView();
            if (state.view !== view || !sameView(settled, view)) {
                scheduleCheck();
                return;
            }
            mount(view)
                .catch((error) => {
                    console.warn("[Streamline] Could not build this page.", error);
                    view.container.classList.remove("sl-takeover");
                    clearPageClasses();
                })
                .finally(() => {
                    view.status = "done";
                });
        }, SETTLE_MS);
    }

    const SETTLE_MS = 60;
    let settleTimer = 0;

    let checkScheduled = false;
    function scheduleCheck() {
        if (checkScheduled) {
            return;
        }
        checkScheduled = true;
        window.requestAnimationFrame(() => {
            checkScheduled = false;
            check();
        });
    }

    // ---------------------------------------------------------------------------------------
    // Phone preview (a testing aid, switched on in the settings): the current page in a
    // phone-sized frame, with Jellyfin's phone layout forced inside it.
    // ---------------------------------------------------------------------------------------

    const IN_PHONE_PREVIEW = new URLSearchParams(window.location.search).get("slphone") === "1";

    function forcePhoneLayout() {
        const html = document.documentElement;
        const apply = () => {
            if (!html.classList.contains("layout-mobile") || html.classList.contains("layout-desktop")) {
                html.classList.remove("layout-desktop", "layout-tv");
                html.classList.add("layout-mobile");
            }
        };
        apply();
        new MutationObserver(apply).observe(html, { attributes: true, attributeFilter: ["class"] });
    }

    function ensurePhonePreviewButton() {
        // Not on the admin dashboard, where it would sit over the sidebar.
        const onDashboard = document.body.classList.contains("dashboardDocument");
        const wanted = state.config && state.config.ShowPhonePreview && !IN_PHONE_PREVIEW && window.top === window && !onDashboard;
        const existing = document.querySelector(".sl-phone-toggle");
        if (!wanted) {
            if (existing) {
                existing.remove();
            }
            return;
        }
        if (existing) {
            return;
        }
        const button = el("button", "sl-phone-toggle");
        button.type = "button";
        button.appendChild(icon("smartphone"));
        button.appendChild(el("span", null, "Phone view"));
        button.addEventListener("click", togglePhonePreview);
        document.body.appendChild(button);
    }

    function togglePhonePreview() {
        const open = document.querySelector(".sl-phone-preview");
        const button = document.querySelector(".sl-phone-toggle");
        if (open) {
            open.remove();
            button.replaceChildren(icon("smartphone"), el("span", null, "Phone view"));
            return;
        }
        const overlay = el("div", "sl-phone-preview");
        const frame = el("div", "sl-phone-frame");
        const iframe = el("iframe", "sl-phone-screen");
        iframe.title = "Phone preview";
        // With the User view open, the phone shows that account too.
        iframe.src = state.userView ? `${window.location.pathname}?slphone=1&slviewas=1#/home` : `${window.location.pathname}?slphone=1${window.location.hash}`;
        frame.appendChild(iframe);
        overlay.appendChild(frame);
        overlay.addEventListener("click", (event) => {
            if (event.target === overlay) {
                togglePhonePreview();
            }
        });
        document.body.appendChild(overlay);
        button.replaceChildren(icon("desktop_windows"), el("span", null, "Back to desktop"));
    }

    function initialize() {
        if (IN_PHONE_PREVIEW) {
            forcePhoneLayout();
        }

        // Reacting to any DOM/class change is deliberately broad: pages are rendered inside a
        // React subtree and shown/hidden by the router in ways no single, narrower mutation
        // reliably signals. check() is debounced to one per frame and cheap when nothing
        // changed; mutations inside our own pages (the banner rotating, rows filling in) are
        // ignored outright.
        const observer = new MutationObserver((mutations) => {
            const preview = state.preview;
            for (const mutation of mutations) {
                const target = mutation.target;
                if ((target.closest && target.closest(".sl-root, .sl-detail-extra, .sl-tabbar, .sl-phone-preview, .sl-phone-toggle, .sl-usermenu, .sl-dash-home, .sl-dialog, .sl-userview")) || (preview && preview.contains(target))) {
                    continue;
                }
                scheduleCheck();
                return;
            }
        });
        observer.observe(document.body, { attributes: true, attributeFilter: ["class"], childList: true, subtree: true });
        window.addEventListener("hashchange", scheduleCheck);
        window.addEventListener("hashchange", () => closeUserMenu());

        // The person icon opens a small menu (Settings, Sign out) instead of going straight to
        // the settings page; it's where those live now that the side menu is admin-only.
        document.addEventListener("click", (event) => {
            const button = event.target.closest && event.target.closest(".headerUserButton");
            if (!button || !userId() || !document.documentElement.classList.contains("sl-nav-on")) {
                return;
            }
            event.preventDefault();
            event.stopImmediatePropagation();
            toggleUserMenu(button);
        }, true);

        window.addEventListener("resize", () => {
            if (state.view && state.root) {
                updatePull(state.view.container);
                state.root.querySelectorAll(".sl-row").forEach((row) => row.updateArrows && row.updateArrows());
            }
        });

        // Switching between phone and desktop layouts swaps posters and landscape cards.
        COMPACT_QUERY.addEventListener("change", () => {
            document.querySelectorAll(".sl-root .sl-row[data-row]").forEach((section) => {
                if (section.slRow) {
                    section.replaceWith(buildRow(section.slRow));
                }
            });
        });

        document.addEventListener("visibilitychange", () => (document.hidden ? pauseHero("tab") : resumeHero("tab")));

        scheduleCheck();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initialize, { once: true });
    } else {
        initialize();
    }
})();
