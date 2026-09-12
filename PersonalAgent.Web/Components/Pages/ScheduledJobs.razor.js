export function timeZone() {
    return Intl.DateTimeFormat().resolvedOptions().timeZone;
}
