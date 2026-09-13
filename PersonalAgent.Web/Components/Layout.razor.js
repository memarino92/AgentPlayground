export function initialize(menu) {
    menu.addEventListener('click', event => {
        if (event.target.closest('a')) menu.hidePopover();
    });
}
