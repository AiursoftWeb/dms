if (navigator.clipboard) {
    for (const block of document.querySelectorAll('[data-onboarding-command]')) {
        const button = block.querySelector('button');
        const command = block.querySelector('code');
        const status = block.querySelector('[role="status"]');
        button.hidden = false;
        button.addEventListener('click', async () => {
            status.textContent = '';
            try {
                await navigator.clipboard.writeText(command.textContent);
                const message = block.querySelector('[data-copied-message]').textContent;
                button.textContent = message;
                status.textContent = message;
            } catch {
                const range = document.createRange();
                range.selectNodeContents(command);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
            }
        });
    }
}
