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

const copyCommand = verification.querySelector('[data-copy-command]');
if (navigator.clipboard) {
    copyCommand.hidden = false;
    copyCommand.addEventListener('click', async () => {
        const command = verification.querySelector('[data-sign-command]');
        try {
            await navigator.clipboard.writeText(command.textContent);
            copyCommand.textContent = verification.querySelector('[data-copy-label]').textContent;
        } catch {
            const range = document.createRange();
            range.selectNodeContents(command);
            window.getSelection().removeAllRanges();
            window.getSelection().addRange(range);
        }
    });
}
