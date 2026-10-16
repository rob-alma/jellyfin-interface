// Streamline: runs before Jellyfin's own code. Only in the admin's "User view" frame
// (?slviewas=1): that frame keeps its sign-in in its own corner of the browser's storage, so it
// can be signed in as the test user without touching the admin's own session in the page
// around it. Everywhere else this does nothing.
(function () {
    "use strict";
    try {
        if (!/[?&]slviewas=1(&|$)/.test(window.location.search)) {
            return;
        }
        var prefix = "streamline-viewas:";
        var real = window.localStorage;
        var proto = Storage.prototype;
        var getItem = proto.getItem;
        var setItem = proto.setItem;
        var removeItem = proto.removeItem;
        proto.getItem = function (key) {
            return getItem.call(this, this === real ? prefix + key : key);
        };
        proto.setItem = function (key, value) {
            return setItem.call(this, this === real ? prefix + key : key, value);
        };
        proto.removeItem = function (key) {
            return removeItem.call(this, this === real ? prefix + key : key);
        };
        window.__streamlineUserView = true;
    } catch (e) {
        // Without the separation the frame isn't opened as another user at all.
    }
})();
