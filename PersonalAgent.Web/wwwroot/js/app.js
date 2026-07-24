window.downloadTextFileFromBase64 = function(fileName, base64, contentType) {
  const binary = atob(base64);
  const len = binary.length;
  const bytes = new Uint8Array(len);
  for (let i = 0; i < len; i += 1) {
    bytes[i] = binary.charCodeAt(i);
  }

  const blob = new Blob([bytes], { type: contentType || "text/plain;charset=utf-8" });
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = fileName || "transcript.txt";
  document.body.appendChild(link);
  link.click();
  document.body.removeChild(link);
  URL.revokeObjectURL(url);
};

window.scrollToChatBottom = function(containerId) {
  const container = document.getElementById(containerId);
  if (!container) return;
  container.scrollTop = container.scrollHeight;
};

window.isNearChatBottom = function(containerId, thresholdPx) {
  const container = document.getElementById(containerId);
  if (!container) return true;
  const threshold = thresholdPx || 80;
  return container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;
};

document.addEventListener("keydown", function(event) {
  if (event.key !== "Enter" || event.shiftKey || !event.target.matches("textarea[data-send-on-enter='true']")) return;
  event.preventDefault();
  event.target.value = "";
}, true);

document.addEventListener("click", function(event) {
  const menuLink = event.target.closest(".navbar-menu__link");
  if (!menuLink) return;
  menuLink.closest("details")?.removeAttribute("open");
});
