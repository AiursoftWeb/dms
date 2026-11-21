const copyKey = document.querySelector('#copy-agent-key');
const key = document.querySelector('#new-agent-key');
copyKey.hidden = false;
copyKey.addEventListener('click', async () => {
    key.focus();
    key.select();
    if (navigator.clipboard) {
        try {
            await navigator.clipboard.writeText(key.value);
            copyKey.textContent = document.querySelector('#agent-key-copied').textContent;
        } catch {
            // The selected key can still be copied using the keyboard.
        }
    }
});
