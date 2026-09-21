(function () {
    function getPrompt(id) {
        var el = document.getElementById(id);
        return el ? el.textContent : '';
    }

    window.GatewayDocsPrompt = {
        copy: async function (id, button) {
            var text = getPrompt(id);
            if (!text) return;
            try {
                await navigator.clipboard.writeText(text);
                if (button) {
                    var original = button.textContent;
                    button.textContent = 'Copied';
                    setTimeout(function () { button.textContent = original; }, 1600);
                }
            } catch (e) {
                if (button) button.textContent = 'Copy failed';
            }
        },
        download: function (id, filename) {
            var text = getPrompt(id);
            if (!text) return;
            var blob = new Blob([text], { type: 'text/markdown;charset=utf-8' });
            var url = URL.createObjectURL(blob);
            var link = document.createElement('a');
            link.href = url;
            link.download = filename || 'arkana-gateway-autonomous-setup.md';
            document.body.appendChild(link);
            link.click();
            link.remove();
            URL.revokeObjectURL(url);
        }
    };
})();
