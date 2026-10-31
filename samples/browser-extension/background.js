const DEFAULTS = {
  endpoint: "http://127.0.0.1:17853/v1/translate",
  token: "",
  target: "简体中文",
};

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: "snip-translate",
    title: "用 Snip 翻译所选内容",
    contexts: ["selection"],
  });
});

chrome.contextMenus.onClicked.addListener(async (info) => {
  if (info.menuItemId !== "snip-translate" || !info.selectionText) {
    return;
  }

  const config = { ...DEFAULTS, ...(await chrome.storage.local.get(DEFAULTS)) };
  if (!config.token) {
    notify("Snip 翻译", "请先在扩展选项里填写 Token。");
    return;
  }

  try {
    const response = await fetch(config.endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Authorization": `Bearer ${config.token}`,
      },
      body: JSON.stringify({
        texts: [info.selectionText],
        target: config.target,
      }),
    });

    const data = await response.json().catch(() => ({}));
    const message = response.ok
      ? (data.translations?.[0] ?? "(空结果)")
      : (data.error ?? `HTTP ${response.status}`);
    notify(response.ok ? "Snip 翻译" : `Snip 翻译失败 (${response.status})`, message);
  } catch (error) {
    notify("Snip 翻译失败", String(error));
  }
});

function notify(title, message) {
  chrome.notifications.create({
    type: "basic",
    iconUrl: "icon128.png",
    title,
    message: String(message).slice(0, 1000),
  });
}
