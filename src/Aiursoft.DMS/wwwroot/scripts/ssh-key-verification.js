const verification = document.querySelector('[data-ssh-key-verification]');
const countdown = verification.querySelector('[data-countdown]');
const expiresAt = Date.now() + Number(verification.dataset.seconds) * 1000;
const timer = window.setInterval(updateCountdown, 250);

function updateCountdown() {
    const remaining = Math.max(0, Math.ceil((expiresAt - Date.now()) / 1000));
    const minutes = String(Math.floor(remaining / 60)).padStart(2, '0');
    const seconds = String(remaining % 60).padStart(2, '0');
    countdown.textContent = `${minutes}:${seconds}`;
    countdown.classList.toggle('text-danger', remaining < 60);
    if (remaining === 0) {
        window.clearInterval(timer);
        verification.querySelector('[data-verification-timer]').hidden = true;
        verification.querySelector('[data-verification-active]').hidden = true;
        verification.querySelector('[data-verification-expired]').hidden = false;
    }
}

updateCountdown();

if (navigator.clipboard) {
    verification.querySelectorAll('[data-copy-command]').forEach(copyCommand => {
        copyCommand.hidden = false;
        copyCommand.addEventListener('click', async () => {
            const container = copyCommand.closest('[data-command-box]') || verification;
            const command = container.querySelector('[data-sign-command]');
            const label = container.querySelector('[data-copy-label]') || verification.querySelector('[data-copy-label]');
            try {
                await navigator.clipboard.writeText(command.textContent.trim());
                const originalText = copyCommand.textContent;
                copyCommand.textContent = label ? label.textContent : 'Copied';
                setTimeout(() => { copyCommand.textContent = originalText; }, 2000);
            } catch {
                const range = document.createRange();
                range.selectNodeContents(command);
                window.getSelection().removeAllRanges();
                window.getSelection().addRange(range);
            }
        });
    });
}
