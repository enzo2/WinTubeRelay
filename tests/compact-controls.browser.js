// Run in the rendered page with DevTools or browser_exec js().
// Check at 320px, 390px and desktop widths; does not control the player.
(() => {
    const failures = [];
    const check = (condition, message) => { if (!condition) failures.push(message); };
    const visible = e => e.getBoundingClientRect().height > 0;
    const firstInput = [...document.querySelectorAll('input')].find(visible);
    check(firstInput?.id === 'urlInput', 'YouTube URL must be the first visible input');
    const settings = document.querySelector('details.connection-settings');
    check(settings && !settings.open, 'Connection settings must be collapsed initially');
    check(settings?.contains(document.getElementById('apiKeyInput')), 'Optional key belongs in settings');
    check(settings?.textContent.includes('optional') && settings.textContent.includes('browser'), 'Explain the optional browser access key');
    check(![...document.querySelectorAll('button')].some(e => /quit/i.test(e.textContent)), 'No ambiguous Quit control');
    check(!document.querySelector('[onclick="quit()"]'), 'No Quit handler exposed');
    const buttons = [...document.querySelectorAll('.play-actions .btn, .playback-command')];
    check(buttons.length === 7, 'Keep Play/Queue and five existing playback controls');
    for (const button of buttons) {
        const box = button.getBoundingClientRect();
        check(box.height >= 44 && box.height <= 48, `${button.textContent.trim()} must be compact but touch-friendly (44–48px), got ${box.height}`);
    }
    const actions = [...document.querySelectorAll('.play-actions .btn')].map(e => e.getBoundingClientRect());
    check(actions.length === 2 && Math.abs(actions[0].top - actions[1].top) < 1, 'Play and Queue share one row on phones too');
    check(document.documentElement.scrollWidth <= document.documentElement.clientWidth, 'No horizontal page overflow');
    if (failures.length) throw new Error(failures.join('\n'));
    return { ok: true, width: innerWidth, buttonHeights: buttons.map(e => e.getBoundingClientRect().height) };
})();
