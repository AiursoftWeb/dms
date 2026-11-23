const machineList = document.querySelector('[data-machine-ssh-list]');

if (machineList) {
    const localTime = new Intl.DateTimeFormat(document.documentElement.lang || undefined, {
        dateStyle: 'short',
        timeStyle: 'short'
    });
    const localNumber = new Intl.NumberFormat(document.documentElement.lang || undefined);

    function localizeTimes(row) {
        for (const time of row.querySelectorAll('time[data-utc-now]')) {
            time.textContent = localTime.format(new Date(time.dateTime));
        }
    }

    function updateTimes() {
        const now = Date.now();
        for (const row of machineList.querySelectorAll('[data-machine-ssh]')) {
            const staleAt = Date.parse(row.dataset.sshStaleAt);
            const unavailable = !Number.isFinite(staleAt) || staleAt <= now;
            row.querySelector('[data-ssh-current]').hidden = unavailable;
            row.querySelector('[data-ssh-unverified]').hidden = !unavailable;
            if (unavailable) continue;

            const countdown = row.querySelector('[data-ssh-until]');
            if (!countdown) continue;
            const minutes = Math.ceil((Date.parse(countdown.dataset.sshUntil) - now) / 60000);
            const expired = minutes <= 0;
            row.querySelector('[data-ssh-before-expiry]').hidden = expired;
            row.querySelector('[data-ssh-after-expiry]').hidden = !expired;
            countdown.hidden = expired;
            if (!expired) countdown.textContent = countdown.dataset.sshMinutes.replace('{0}', localNumber.format(minutes));
        }
    }

    async function refreshSshStates() {
        const rows = new Map(Array.from(machineList.querySelectorAll('[data-machine-ssh]'),
            row => [row.dataset.machineSsh, row]));
        if (rows.size === 0 || document.hidden) return;
        const url = new URL(machineList.dataset.machineSshList, window.location.href);
        for (const id of rows.keys()) url.searchParams.append('id', id);

        try {
            const response = await fetch(url, { redirect: 'error', cache: 'no-store' });
            if (!response.ok) return;
            const fragment = new DOMParser().parseFromString(await response.text(), 'text/html');
            for (const current of fragment.querySelectorAll('[data-machine-ssh]')) {
                const previous = rows.get(current.dataset.machineSsh);
                if (!previous || previous.dataset.sshStaleAt === current.dataset.sshStaleAt) continue;
                localizeTimes(current);
                const restoreFocus = previous.contains(document.activeElement);
                previous.replaceWith(current);
                if (restoreFocus) current.querySelector('a')?.focus();
            }
            updateTimes();
        } catch {
            updateTimes();
        }
    }

    localizeTimes(machineList);
    updateTimes();
    window.setInterval(updateTimes, 1000);
    window.setInterval(refreshSshStates, 30000);
}
