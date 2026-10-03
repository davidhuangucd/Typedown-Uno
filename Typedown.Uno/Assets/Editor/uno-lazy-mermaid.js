// mermaid.min.js is 3.5 MB of script, and loading it from index.html made every start parse it first (about a
// quarter of a second) although few documents draw a diagram. The editor looks the library up as window.mermaid
// only when it draws one, and polls until it is there, so the first look-up is what loads it. index.html loads
// this file in place of mermaid.min.js (Tools/sync-editor.sh puts it there).
(function () {
    var requested = false;
    Object.defineProperty(window, 'mermaid', {
        configurable: true,
        get: function () {
            if (!requested) {
                requested = true;
                var script = document.createElement('script');
                script.src = './mermaid.min.js';
                document.head.appendChild(script);
            }
            return undefined;
        },
        // mermaid.min.js ends with globalThis["mermaid"] = ...: from then on it is a plain property.
        set: function (value) {
            Object.defineProperty(window, 'mermaid', { value: value, writable: true, configurable: true, enumerable: true });
        }
    });
})();
