const localTime = new Intl.DateTimeFormat(document.documentElement.lang || undefined, {
    dateStyle: 'medium',
    timeStyle: 'short'
});

for (const timestamp of document.querySelectorAll('.lockbox-review time[data-utc-now]')) {
    timestamp.textContent = localTime.format(new Date(timestamp.dateTime));
}

if (navigator.clipboard) {
    for (const terminal of document.querySelectorAll('[data-lockbox-terminal]')) {
        const button = terminal.querySelector('[data-copy-command]');
        const command = terminal.querySelector('[data-command]');
        button.hidden = false;
        button.addEventListener('click', async () => {
            try {
                await navigator.clipboard.writeText(command.textContent);
                button.querySelector('span').textContent = terminal.querySelector('[data-key="command-copied"]')?.textContent || "Command copied";
            } catch {
                const range = document.createRange();
                range.selectNodeContents(command);
                window.getSelection().removeAllRanges();
                window.getSelection().addRange(range);
            }
        });
    }
}
